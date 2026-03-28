using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime.Descriptors;

namespace Mio.Script;

/// <summary>
/// Exposes an AngleSharp IDocument to Jint as a JS document object.
/// Extends ObjectInstance so that React's __reactContainer$* and other arbitrary
/// JS properties persist in the Jint property bag.
///
/// IMPORTANT: All methods that return DOM elements MUST return a cached JsValue via WrapJsValue().
/// </summary>
public sealed class DocumentShim : ObjectInstance
{
    private readonly IDocument _doc;

    private readonly Dictionary<IElement, JsValue> _jsValueCache = [];
    private readonly Dictionary<IElement, ElementShim> _shimCache = [];
    private readonly Dictionary<INode, JsValue> _textCache = [];
    private readonly Dictionary<INode, JsValue> _commentCache = [];

    /// <summary>Shared listener storage keyed by IElement.</summary>
    internal readonly Dictionary<IElement, Dictionary<string, List<JsValue>>> Listeners = [];

    /// <summary>Document-level event listeners (React delegates events here).</summary>
    private readonly Dictionary<string, List<JsValue>> _docListeners = [];

    /// <summary>Explicit storage for unknown JS properties (React's __reactContainer$*, etc.).</summary>
    private readonly Dictionary<string, JsValue> _extraProps = new();

    public event Action? DomMutated;

    private JsValue? _getElementByIdFn, _querySelectorFn, _querySelectorAllFn;
    private JsValue? _createElementFn, _createElementNsFn;
    private JsValue? _createTextNodeFn, _createCommentFn, _createDocumentFragmentFn;
    private JsValue? _createEventFn;
    private JsValue? _addEventListenerFn, _removeEventListenerFn, _dispatchEventFn;

    public DocumentShim(IDocument doc, Engine engine) : base(engine)
    {
        _doc = doc;
    }

    internal Dictionary<string, List<JsValue>> GetListeners(IElement el)
    {
        if (!Listeners.TryGetValue(el, out var dict))
            Listeners[el] = dict = [];
        return dict;
    }

    private bool _inGetOwnProp;
    public override PropertyDescriptor GetOwnProperty(JsValue property)
    {
        if (_inGetOwnProp) return base.GetOwnProperty(property);
        _inGetOwnProp = true;
        try
        {
            var v = Get(property, this);
            return !v.IsUndefined() ? new PropertyDescriptor(v, true, true, true) : base.GetOwnProperty(property);
        }
        finally { _inGetOwnProp = false; }
    }

    public override JsValue Get(JsValue property, JsValue receiver)
    {
        if (!property.IsString()) return base.Get(property, receiver);
        return property.AsString() switch
        {
            "nodeType" => (JsValue)9,
            "nodeName" => (JsValue)"#document",
            "body" => _doc.Body != null ? WrapJsValue(_doc.Body) : JsValue.Null,
            "head" => _doc.Head != null ? WrapJsValue(_doc.Head) : JsValue.Null,
            "documentElement" => _doc.DocumentElement != null ? WrapJsValue(_doc.DocumentElement) : JsValue.Null,
            "title" => (JsValue)(_doc.Title ?? ""),
            "readyState" => (JsValue)"complete",
            "compatMode" => (JsValue)"CSS1Compat",
            "contentType" => (JsValue)"text/html",
            "childNodes" => BuildJsArray(_doc.DocumentElement != null
                                    ? [WrapJsValue(_doc.DocumentElement)]
                                    : []),
            "firstChild" => _doc.DocumentElement != null ? WrapJsValue(_doc.DocumentElement) : JsValue.Null,
            "defaultView" => Engine.GetValue("window"),

            "getElementById" => _getElementByIdFn ??= JsValue.FromObject(Engine,
                new Func<string, JsValue>(GetElementById)),
            "querySelector" => _querySelectorFn ??= JsValue.FromObject(Engine,
                new Func<string, JsValue>(QuerySelector)),
            "querySelectorAll" => _querySelectorAllFn ??= JsValue.FromObject(Engine,
                new Func<string, JsValue>(QuerySelectorAll)),
            "createElement" => _createElementFn ??= JsValue.FromObject(Engine,
                new Func<string, JsValue, JsValue>(CreateElement)),
            "createElementNS" => _createElementNsFn ??= JsValue.FromObject(Engine,
                new Func<string, string, JsValue>(CreateElementNS)),
            "createTextNode" => _createTextNodeFn ??= JsValue.FromObject(Engine,
                new Func<string, JsValue>(CreateTextNode)),
            "createComment" => _createCommentFn ??= JsValue.FromObject(Engine,
                new Func<string, JsValue>(CreateComment)),
            "createDocumentFragment" => _createDocumentFragmentFn ??= JsValue.FromObject(Engine,
                new Func<JsValue>(CreateDocumentFragment)),
            "createEvent" => _createEventFn ??= JsValue.FromObject(Engine,
                new Func<string, JsValue>(CreateEvent)),
            "addEventListener" => _addEventListenerFn ??= JsValue.FromObject(Engine,
                new Action<string, JsValue, JsValue>((t, h, _) => AddEventListener(t, h))),
            "removeEventListener" => _removeEventListenerFn ??= JsValue.FromObject(Engine,
                new Action<string, JsValue, JsValue>((t, h, _) => RemoveEventListener(t, h))),
            "dispatchEvent" => _dispatchEventFn ??= JsValue.FromObject(Engine,
                new Func<JsValue, bool>(DispatchEvent)),

            _ => _extraProps.TryGetValue(property.AsString(), out var stored) ? stored : base.Get(property, receiver)
        };
    }

    public override bool Set(JsValue property, JsValue value, JsValue receiver)
    {
        if (!property.IsString()) return base.Set(property, value, receiver);
        switch (property.AsString())
        {
            case "title": _doc.Title = value.ToString(); return true;
            default: _extraProps[property.AsString()] = value; return true;
        }
    }

    private void AddEventListener(string type, JsValue handler)
    {
        if (!_docListeners.TryGetValue(type, out var list))
            _docListeners[type] = list = [];
        list.Add(handler);
    }

    private void RemoveEventListener(string type, JsValue handler)
    {
        if (_docListeners.TryGetValue(type, out var list))
            list.Remove(handler);
    }

    private bool DispatchEvent(JsValue eventObj)
    {
        var type = eventObj.AsObject()["type"].AsString();
        if (_docListeners.TryGetValue(type, out var list))
        {
            foreach (var fn in list.ToList())
            {
                try { Engine.Invoke(fn, JsValue.Undefined, [eventObj]); }
                catch (Exception ex) { Console.Error.WriteLine($"[DocEvent:{type}] {ex.Message}"); }
            }
        }
        return true;
    }

    /// <summary>Fires document-level listeners for an event that bubbled up from an element.</summary>
    internal void FireDocumentListeners(string type, JsValue eventObj)
    {
        if (_docListeners.TryGetValue(type, out var list))
        {
            foreach (var fn in list.ToList())
            {
                try { Engine.Invoke(fn, JsValue.Undefined, [eventObj]); }
                catch (Exception ex) { Console.Error.WriteLine($"[DocEvent:{type}] {ex.Message}"); }
            }
        }
    }

    private JsValue CreateEvent(string type)
    {
        return Engine.Evaluate($@"(function(){{
            var e = {{type:'{type}',bubbles:false,cancelable:false,defaultPrevented:false,
                target:null,currentTarget:null,eventPhase:0,
                preventDefault:function(){{this.defaultPrevented=true}},
                stopPropagation:function(){{this._stopped=true}},
                stopImmediatePropagation:function(){{this._stopped=true}},
                initEvent:function(t,b,c){{this.type=t;this.bubbles=!!b;this.cancelable=!!c}}
            }};
            return e;
        }})()");
    }

    internal ElementShim Wrap(IElement el)
    {
        if (!_shimCache.TryGetValue(el, out var shim))
        {
            shim = new ElementShim(el, this, Engine);
            _shimCache[el] = shim;
            _jsValueCache[el] = shim;
        }
        return shim;
    }

    internal JsValue WrapJsValue(IElement el)
    {
        if (!_jsValueCache.TryGetValue(el, out var v))
        {
            var shim = new ElementShim(el, this, Engine);
            _shimCache[el] = shim;
            _jsValueCache[el] = v = shim;
        }
        return v;
    }

    internal void NotifyMutation() => DomMutated?.Invoke();

    internal void PurgeNodeCache(INode node)
    {
        if (node is IElement el)
        {
            _jsValueCache.Remove(el);
            _shimCache.Remove(el);
            Listeners.Remove(el);
        }
        else
        {
            _textCache.Remove(node);
            _commentCache.Remove(node);
        }
    }

    internal JsValue WrapNode(INode? node)
    {
        if (node == null) return JsValue.Null;
        return node switch
        {
            IElement el => WrapJsValue(el),
            IComment co => WrapComment(co),
            IText tx => WrapText(tx),
            _ => JsValue.Null,
        };
    }

    private JsValue WrapComment(ICharacterData co)
    {
        if (!_commentCache.TryGetValue(co, out var v))
            _commentCache[co] = v = new CommentShim(co, this, Engine);
        return v;
    }

    private JsValue WrapText(INode tx)
    {
        if (!_textCache.TryGetValue(tx, out var v))
            _textCache[tx] = v = new TextNodeShim(tx, this, Engine);
        return v;
    }

    internal JsValue BuildJsArray(IEnumerable<JsValue> items)
    {
        var list = items.ToList();
        if (list.Count == 0) return Engine.Evaluate("[]");
        Engine.SetValue("__arrBld__", new Func<int, JsValue>(i => i < list.Count ? list[i] : JsValue.Undefined));
        var result = Engine.Evaluate($"(function(){{var a=[];for(var i=0;i<{list.Count};i++)a.push(__arrBld__(i));return a;}})()");
        Engine.SetValue("__arrBld__", JsValue.Undefined);
        return result;
    }

    internal static INode? ExtractNode(JsValue v)
    {
        if (v.IsNull() || v.IsUndefined()) return null;
        if (v is ElementShim es) return es.NativeElement;
        if (v is CommentShim cs) return cs.NativeNode;
        if (v is TextNodeShim ts) return ts.NativeNode;
        if (v is DocumentFragmentShim fs) return null; // fragments handled specially
        return null;
    }

    private JsValue GetElementById(string id)
    {
        var el = _doc.GetElementById(id);
        return el != null ? WrapJsValue(el) : JsValue.Null;
    }

    private JsValue QuerySelector(string selector)
    {
        try
        {
            var el = _doc.QuerySelector(selector);
            return el != null ? WrapJsValue(el) : JsValue.Null;
        }
        catch { return JsValue.Null; }
    }

    private JsValue QuerySelectorAll(string selector)
    {
        try
        {
            var elements = _doc.QuerySelectorAll(selector);
            return BuildJsArray(elements.Select(e => WrapJsValue(e)));
        }
        catch { return Engine.Evaluate("[]"); }
    }

    private JsValue CreateElement(string tagName, JsValue options)
    {
        var el = _doc.CreateElement(tagName.ToLowerInvariant());
        NotifyMutation();
        return WrapJsValue(el);
    }

    private JsValue CreateElementNS(string? ns, string tagName)
    {
        var el = _doc.CreateElement(tagName.ToLowerInvariant());
        if (!string.IsNullOrEmpty(ns))
            el.SetAttribute("data-ns", ns);
        NotifyMutation();
        return WrapJsValue(el);
    }

    private JsValue CreateTextNode(string text)
    {
        var textNode = _doc.CreateTextNode(text);
        return WrapText(textNode);
    }

    private JsValue CreateComment(string text)
    {
        var comment = _doc.CreateComment(text);
        return WrapComment(comment);
    }

    private JsValue CreateDocumentFragment()
    {
        return new DocumentFragmentShim(this, Engine);
    }
}

/// <summary>
/// Wraps an AngleSharp IElement for JS interop.
/// Extends ObjectInstance so that unknown JS properties (Vue's _vnode, __vue_app__, _vei,
/// React's __reactFiber$*, __reactProps$*, etc.) persist in the Jint property bag.
/// </summary>
public sealed class ElementShim : ObjectInstance
{
    private readonly IElement _el;
    private readonly DocumentShim _doc;
    private JsValue? _styleDecl;
    private JsValue? _classListVal;
    private JsValue? _getAttributeFn, _setAttributeFn, _hasAttributeFn, _removeAttributeFn;
    private JsValue? _appendChildFn, _removeChildFn, _replaceChildFn, _insertBeforeFn;
    private JsValue? _addEventListenerFn, _removeEventListenerFn, _dispatchEventFn;
    private JsValue? _querySelectorFn, _querySelectorAllFn;
    private JsValue? _cloneNodeFn, _removeFn, _containsFn;
    private JsValue? _getBoundingClientRectFn, _getRootNodeFn;
    private JsValue? _focusFn, _blurFn, _closestFn, _matchesFn;
    private JsValue? _getAttributeNamesFn, _compareDocPositionFn;

    /// <summary>
    /// Explicit storage for unknown JS properties (Vue's _vnode, __vue_app__, _vei, React fiber, etc.).
    /// </summary>
    private readonly Dictionary<string, JsValue> _extraProps = new();

    internal IElement NativeElement => _el;

    internal ElementShim(IElement element, DocumentShim doc, Engine engine) : base(engine)
    {
        _el = element;
        _doc = doc;
    }

    private bool _inGetOwnProp;
    public override PropertyDescriptor GetOwnProperty(JsValue property)
    {
        if (_inGetOwnProp) return base.GetOwnProperty(property);
        _inGetOwnProp = true;
        try
        {
            var v = Get(property, this);
            return !v.IsUndefined() ? new PropertyDescriptor(v, true, true, true) : base.GetOwnProperty(property);
        }
        finally { _inGetOwnProp = false; }
    }

    public override JsValue Get(JsValue property, JsValue receiver)
    {
        if (!property.IsString()) return base.Get(property, receiver);
        return property.AsString() switch
        {
            "tagName" => (JsValue)_el.TagName.ToUpperInvariant(),
            "localName" => (JsValue)_el.TagName.ToLowerInvariant(),
            "nodeName" => (JsValue)_el.TagName.ToUpperInvariant(),
            "nodeType" => (JsValue)1,
            "id" => (JsValue)(_el.Id ?? ""),
            "className" => (JsValue)(_el.ClassName ?? ""),
            "innerHTML" => (JsValue)(_el.InnerHtml ?? ""),
            "outerHTML" => (JsValue)(_el.OuterHtml ?? ""),
            "textContent" => (JsValue)(_el.TextContent ?? ""),
            "innerText" => (JsValue)(_el.TextContent ?? ""),
            "value" => (JsValue)(_el.GetAttribute("value") ?? ""),
            "checked" => (JsValue)(_el.GetAttribute("checked") != null),
            "disabled" => (JsValue)(_el.GetAttribute("disabled") != null),
            "type" => (JsValue)(_el.GetAttribute("type") ?? ""),
            "href" => (JsValue)(_el.GetAttribute("href") ?? ""),
            "src" => (JsValue)(_el.GetAttribute("src") ?? ""),
            "isConnected" => (JsValue)(_el.Owner != null && _el.ParentElement != null),
            "namespaceURI" => (JsValue)(_el.GetAttribute("data-ns") ?? "http://www.w3.org/1999/xhtml"),
            "childElementCount" => (JsValue)_el.ChildElementCount,
            "nodeValue" => JsValue.Null,  // spec: nodeValue is null for Element nodes
            "attributes" => BuildAttributes(),
            "dataset" => BuildDataset(),
            "style" => _styleDecl ??= new CssStyleDeclaration(Engine, _el, _doc),
            "classList" => _classListVal ??= new ClassListShim(_el, _doc, Engine),
            "parentElement" => _el.ParentElement != null ? _doc.WrapJsValue(_el.ParentElement) : JsValue.Null,
            "parentNode" => _el.ParentElement != null ? _doc.WrapJsValue(_el.ParentElement) : JsValue.Null,
            "firstChild" => _doc.WrapNode(_el.FirstChild),
            "lastChild" => _doc.WrapNode(_el.LastChild),
            "firstElementChild" => _el.FirstElementChild != null ? _doc.WrapJsValue(_el.FirstElementChild) : JsValue.Null,
            "lastElementChild" => _el.LastElementChild != null ? _doc.WrapJsValue(_el.LastElementChild) : JsValue.Null,
            "nextSibling" => _doc.WrapNode(_el.NextSibling),
            "previousSibling" => _doc.WrapNode(_el.PreviousSibling),
            "nextElementSibling" => _el.NextElementSibling != null ? _doc.WrapJsValue(_el.NextElementSibling) : JsValue.Null,
            "previousElementSibling" => _el.PreviousElementSibling != null ? _doc.WrapJsValue(_el.PreviousElementSibling) : JsValue.Null,
            "children" => _doc.BuildJsArray(_el.Children.Select(c => _doc.WrapJsValue(c))),
            "childNodes" => _doc.BuildJsArray(_el.ChildNodes.Select(n => _doc.WrapNode(n))),
            "ownerDocument" => _doc,   // DocumentShim IS ObjectInstance IS JsValue
            "getAttribute" => _getAttributeFn ??= JsValue.FromObject(Engine,
                new Func<string, JsValue>(n => { var v = _el.GetAttribute(n); return v != null ? (JsValue)v : JsValue.Null; })),
            "setAttribute" => _setAttributeFn ??= JsValue.FromObject(Engine,
                new Action<string, string>((n, v) => { _el.SetAttribute(n, v); _doc.NotifyMutation(); })),
            "hasAttribute" => _hasAttributeFn ??= JsValue.FromObject(Engine,
                new Func<string, bool>(n => _el.HasAttribute(n))),
            "removeAttribute" => _removeAttributeFn ??= JsValue.FromObject(Engine,
                new Action<string>(n => { _el.RemoveAttribute(n); _doc.NotifyMutation(); })),
            "appendChild" => _appendChildFn ??= JsValue.FromObject(Engine,
                new Func<JsValue, JsValue>(AppendChildImpl)),
            "removeChild" => _removeChildFn ??= JsValue.FromObject(Engine,
                new Func<JsValue, JsValue>(RemoveChildImpl)),
            "replaceChild" => _replaceChildFn ??= JsValue.FromObject(Engine,
                new Func<JsValue, JsValue, JsValue>(ReplaceChildImpl)),
            "insertBefore" => _insertBeforeFn ??= JsValue.FromObject(Engine,
                new Func<JsValue, JsValue, JsValue>(InsertBeforeImpl)),
            "addEventListener" => _addEventListenerFn ??= JsValue.FromObject(Engine,
                new Action<string, JsValue, JsValue>((t, h, _) => AddEvtListener(t, h))),
            "removeEventListener" => _removeEventListenerFn ??= JsValue.FromObject(Engine,
                new Action<string, JsValue, JsValue>((t, h, _) => RemoveEvtListener(t, h))),
            "dispatchEvent" => _dispatchEventFn ??= JsValue.FromObject(Engine,
                new Func<JsValue, bool>(DispatchEventImpl)),
            "querySelector" => _querySelectorFn ??= JsValue.FromObject(Engine,
                new Func<string, JsValue>(QuerySelectorImpl)),
            "querySelectorAll" => _querySelectorAllFn ??= JsValue.FromObject(Engine,
                new Func<string, JsValue>(QuerySelectorAllImpl)),
            "cloneNode" => _cloneNodeFn ??= JsValue.FromObject(Engine,
                new Func<JsValue, JsValue>(d => CloneNodeImpl(d.IsBoolean() && d.AsBoolean()))),
            "remove" => _removeFn ??= JsValue.FromObject(Engine,
                new Action(() => { _el.Remove(); _doc.NotifyMutation(); })),
            "contains" => _containsFn ??= JsValue.FromObject(Engine,
                new Func<JsValue, bool>(ContainsImpl)),
            "getBoundingClientRect" => _getBoundingClientRectFn ??= JsValue.FromObject(Engine,
                new Func<JsValue>(() => Engine.Evaluate("({left:0,top:0,right:0,bottom:0,width:0,height:0,x:0,y:0})"))),
            "getRootNode" => _getRootNodeFn ??= JsValue.FromObject(Engine,
                new Func<JsValue>(() => _doc)),
            "focus" => _focusFn ??= JsValue.FromObject(Engine, new Action(() => { })),
            "blur" => _blurFn ??= JsValue.FromObject(Engine, new Action(() => { })),
            "closest" => _closestFn ??= JsValue.FromObject(Engine,
                new Func<string, JsValue>(ClosestImpl)),
            "matches" => _matchesFn ??= JsValue.FromObject(Engine,
                new Func<string, bool>(MatchesImpl)),
            "getAttributeNS" => JsValue.FromObject(Engine,
                new Func<JsValue, string, JsValue>((_, n) => { var v = _el.GetAttribute(n); return v != null ? (JsValue)v : JsValue.Null; })),
            "setAttributeNS" => JsValue.FromObject(Engine,
                new Action<JsValue, string, string>((_, n, v) => { _el.SetAttribute(n, v); _doc.NotifyMutation(); })),
            "removeAttributeNS" => JsValue.FromObject(Engine,
                new Action<JsValue, string>((_, n) => { _el.RemoveAttribute(n); _doc.NotifyMutation(); })),
            "hasChildNodes" => JsValue.FromObject(Engine, new Func<bool>(() => _el.HasChildNodes)),
            "normalize" => JsValue.FromObject(Engine, new Action(() => _el.Normalize())),
            "getAttributeNames" => _getAttributeNamesFn ??= JsValue.FromObject(Engine,
                new Func<JsValue>(() => _doc.BuildJsArray(_el.Attributes.Select(a => (JsValue)a.Name)))),
            "compareDocumentPosition" => _compareDocPositionFn ??= JsValue.FromObject(Engine,
                new Func<JsValue, int>(CompareDocPosition)),
            _ => _extraProps.TryGetValue(property.AsString(), out var stored) ? stored : base.Get(property, receiver)
        };
    }

    public override bool Set(JsValue property, JsValue value, JsValue receiver)
    {
        if (!property.IsString()) return base.Set(property, value, receiver);
        var str = value.IsNull() || value.IsUndefined() ? "" : value.ToString();
        switch (property.AsString())
        {
            case "id": _el.Id = str; _doc.NotifyMutation(); return true;
            case "className": _el.ClassName = str; _doc.NotifyMutation(); return true;
            case "innerHTML": _el.InnerHtml = str; _doc.NotifyMutation(); return true;
            case "textContent": _el.TextContent = str; _doc.NotifyMutation(); return true;
            case "innerText": _el.TextContent = str; _doc.NotifyMutation(); return true;
            case "value": _el.SetAttribute("value", str); _doc.NotifyMutation(); return true;
            case "checked": if (value.IsBoolean() && value.AsBoolean()) _el.SetAttribute("checked", ""); else _el.RemoveAttribute("checked"); return true;
            case "disabled": if (value.IsBoolean() && value.AsBoolean()) _el.SetAttribute("disabled", ""); else _el.RemoveAttribute("disabled"); return true;
            case "nodeValue": return true; // no-op for elements (spec-compliant)
            default: _extraProps[property.AsString()] = value; return true;
        }
    }

    private JsValue AppendChildImpl(JsValue child)
    {
        // Handle DocumentFragment: append all children
        if (child is DocumentFragmentShim frag)
        {
            foreach (var fragChild in frag.TakeChildren())
            {
                var n = DocumentShim.ExtractNode(fragChild);
                if (n != null) _el.AppendChild(n);
            }
            _doc.NotifyMutation();
            return child;
        }

        var node = DocumentShim.ExtractNode(child);
        if (node != null) { _el.AppendChild(node); _doc.NotifyMutation(); }
        return child;
    }

    private JsValue RemoveChildImpl(JsValue child)
    {
        var node = DocumentShim.ExtractNode(child);
        if (node != null)
        {
            _el.RemoveChild(node);
            _doc.PurgeNodeCache(node);
            _doc.NotifyMutation();
        }
        return child;
    }

    private JsValue ReplaceChildImpl(JsValue newChild, JsValue oldChild)
    {
        var n = DocumentShim.ExtractNode(newChild);
        var o = DocumentShim.ExtractNode(oldChild);
        if (n != null && o != null)
        {
            _el.ReplaceChild(n, o);
            _doc.PurgeNodeCache(o);
            _doc.NotifyMutation();
        }
        return oldChild;
    }

    private JsValue InsertBeforeImpl(JsValue newChild, JsValue refChild)
    {
        // Handle DocumentFragment
        if (newChild is DocumentFragmentShim frag)
        {
            var r = DocumentShim.ExtractNode(refChild);
            foreach (var fragChild in frag.TakeChildren())
            {
                var n = DocumentShim.ExtractNode(fragChild);
                if (n == null) continue;
                if (r != null) _el.InsertBefore(n, r);
                else _el.AppendChild(n);
            }
            _doc.NotifyMutation();
            return newChild;
        }

        var node = DocumentShim.ExtractNode(newChild);
        if (node == null) return newChild;
        var refNode = DocumentShim.ExtractNode(refChild);
        if (refNode != null) _el.InsertBefore(node, refNode);
        else _el.AppendChild(node);
        _doc.NotifyMutation();
        return newChild;
    }

    private void AddEvtListener(string type, JsValue handler)
    {
        var listeners = _doc.GetListeners(_el);
        if (!listeners.TryGetValue(type, out var list)) listeners[type] = list = [];
        list.Add(handler);
    }

    private void RemoveEvtListener(string type, JsValue handler)
    {
        if (_doc.GetListeners(_el).TryGetValue(type, out var list)) list.Remove(handler);
    }

    private bool DispatchEventImpl(JsValue eventObj)
    {
        var evtObj = eventObj.AsObject();
        var type = evtObj["type"].AsString();
        var bubbles = evtObj.HasProperty("bubbles") &&
                      !evtObj["bubbles"].IsUndefined() &&
                      evtObj["bubbles"].AsBoolean();

        // Set target
        evtObj.Set("target", this);
        evtObj.Set("currentTarget", this);

        // Fire on target
        FireEvent(type, eventObj);

        // Bubble up through DOM tree
        if (bubbles)
        {
            var current = _el.ParentElement;
            while (current != null)
            {
                var shim = _doc.Wrap(current);
                evtObj.Set("currentTarget", shim);

                // Check stopPropagation
                if (evtObj.HasProperty("_stopped") && evtObj["_stopped"].AsBoolean())
                    break;

                shim.FireEvent(type, eventObj);
                current = current.ParentElement;
            }

            // Bubble to document
            if (!evtObj.HasProperty("_stopped") || !evtObj["_stopped"].AsBoolean())
            {
                evtObj.Set("currentTarget", _doc);
                _doc.FireDocumentListeners(type, eventObj);
            }
        }

        return !(evtObj.HasProperty("defaultPrevented") && evtObj["defaultPrevented"].AsBoolean());
    }

    private JsValue QuerySelectorImpl(string selector)
    {
        try
        {
            var el = _el.QuerySelector(selector);
            return el != null ? _doc.WrapJsValue(el) : JsValue.Null;
        }
        catch { return JsValue.Null; }
    }

    private JsValue QuerySelectorAllImpl(string selector)
    {
        try
        {
            return _doc.BuildJsArray(_el.QuerySelectorAll(selector).Select(e => _doc.WrapJsValue(e)));
        }
        catch { return Engine.Evaluate("[]"); }
    }

    private JsValue CloneNodeImpl(bool deep)
    {
        try { return _doc.WrapJsValue((IElement)_el.Clone(deep)); }
        catch { return JsValue.Null; }
    }

    private bool ContainsImpl(JsValue other)
    {
        if (other.IsNull() || other.IsUndefined()) return false;
        if (other is ElementShim shim) return _el.Contains(shim.NativeElement);
        if (other is TextNodeShim ts) return _el.Contains(ts.NativeNode);
        if (other is CommentShim cs) return _el.Contains(cs.NativeNode);
        return false;
    }

    private JsValue ClosestImpl(string selector)
    {
        try
        {
            var el = _el.Closest(selector);
            return el != null ? _doc.WrapJsValue(el) : JsValue.Null;
        }
        catch { return JsValue.Null; }
    }

    private bool MatchesImpl(string selector)
    {
        try { return _el.Matches(selector); }
        catch { return false; }
    }

    private int CompareDocPosition(JsValue other)
    {
        // DOM spec: returns bitmask (1=disconnected, 2=preceding, 4=following, 8=contains, 16=contained_by)
        if (other is ElementShim otherShim)
        {
            if (ReferenceEquals(_el, otherShim._el)) return 0;
            if (_el.Contains(otherShim._el)) return 16 | 4; // DOCUMENT_POSITION_CONTAINED_BY | FOLLOWING
            if (otherShim._el.Contains(_el)) return 8 | 2;  // DOCUMENT_POSITION_CONTAINS | PRECEDING
            return 4; // FOLLOWING (fallback)
        }
        if (other is TextNodeShim ts && _el.Contains(ts.NativeNode)) return 16 | 4;
        if (other is CommentShim cs && _el.Contains(cs.NativeNode)) return 16 | 4;
        return 1; // DISCONNECTED
    }

    private JsValue BuildAttributes()
    {
        // Build a NamedNodeMap-like object: array-like with named properties + length
        var attrs = _el.Attributes;
        var items = new List<string>();
        foreach (var a in attrs)
            items.Add($"{{name:'{EscapeJs(a.Name)}',value:'{EscapeJs(a.Value ?? "")}',nodeName:'{EscapeJs(a.Name)}',nodeValue:'{EscapeJs(a.Value ?? "")}'}}");

        var script = $"(function(){{var a=[{string.Join(",", items)}];a.getNamedItem=function(n){{for(var i=0;i<a.length;i++)if(a[i].name===n)return a[i];return null}};a.item=function(i){{return a[i]||null}};return a}})()";
        return Engine.Evaluate(script);
    }

    private JsValue BuildDataset()
    {
        // Build a DOMStringMap-like object from data-* attributes
        var parts = new List<string>();
        foreach (var a in _el.Attributes)
        {
            if (!a.Name.StartsWith("data-")) continue;
            var key = a.Name[5..]; // strip "data-"
            // Convert kebab-case to camelCase
            var camel = Regex.Replace(key, @"-(\w)", m => m.Groups[1].Value.ToUpper());
            parts.Add($"'{EscapeJs(camel)}':'{EscapeJs(a.Value ?? "")}'");
        }
        return Engine.Evaluate($"({{{string.Join(",", parts)}}})");
    }

    private static string EscapeJs(string s) =>
        s.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\n", "\\n").Replace("\r", "\\r");

    /// <summary>Fires a DOM event on this element.</summary>
    internal void FireEvent(string type, JsValue? eventArg = null)
    {
        var listeners = _doc.GetListeners(_el);
        var arg = eventArg ?? Engine.Evaluate(
            $"({{type:'{type}',target:null,currentTarget:null,defaultPrevented:false,preventDefault:function(){{this.defaultPrevented=true}},stopPropagation:function(){{this._stopped=true}}}})");

        var attrHandler = _el.GetAttribute("on" + type);
        if (!string.IsNullOrEmpty(attrHandler))
        {
            try { Engine.Execute(attrHandler); }
            catch (Exception ex) { Console.Error.WriteLine($"[Event:{type} inline] {ex.Message}"); }
        }

        if (!listeners.TryGetValue(type, out var list)) return;
        foreach (var fn in list.ToList())
        {
            try { Engine.Invoke(fn, JsValue.Undefined, [arg]); }
            catch (Exception ex) { Console.Error.WriteLine($"[Event:{type}] {ex.Message}"); }
        }
    }
}

/// <summary>
/// Proper DocumentFragment shim as ObjectInstance.
/// Holds children in memory until appended to a real element.
/// </summary>
public sealed class DocumentFragmentShim : ObjectInstance
{
    private readonly DocumentShim _doc;
    private readonly List<JsValue> _children = [];

    private JsValue? _appendChildFn, _insertBeforeFn, _removeChildFn;

    internal DocumentFragmentShim(DocumentShim doc, Engine engine) : base(engine)
    {
        _doc = doc;
    }

    private bool _inGetOwnProp;
    public override PropertyDescriptor GetOwnProperty(JsValue property)
    {
        if (_inGetOwnProp) return base.GetOwnProperty(property);
        _inGetOwnProp = true;
        try
        {
            var v = Get(property, this);
            return !v.IsUndefined() ? new PropertyDescriptor(v, true, true, true) : base.GetOwnProperty(property);
        }
        finally { _inGetOwnProp = false; }
    }

    /// <summary>Takes and clears all children (used when appending fragment to a parent).</summary>
    internal List<JsValue> TakeChildren()
    {
        var result = new List<JsValue>(_children);
        _children.Clear();
        return result;
    }

    public override JsValue Get(JsValue property, JsValue receiver)
    {
        if (!property.IsString()) return base.Get(property, receiver);
        return property.AsString() switch
        {
            "nodeType" => (JsValue)11,
            "nodeName" => (JsValue)"#document-fragment",
            "childNodes" => _doc.BuildJsArray(_children),
            "children" => _doc.BuildJsArray(_children.Where(c => c is ElementShim)),
            "firstChild" => _children.Count > 0 ? _children[0] : JsValue.Null,
            "lastChild" => _children.Count > 0 ? _children[^1] : JsValue.Null,
            "textContent" => (JsValue)string.Join("", _children.Select(GetTextContent)),
            "appendChild" => _appendChildFn ??= JsValue.FromObject(Engine,
                new Func<JsValue, JsValue>(c => { _children.Add(c); return c; })),
            "insertBefore" => _insertBeforeFn ??= JsValue.FromObject(Engine,
                new Func<JsValue, JsValue, JsValue>((n, r) =>
                {
                    if (r == null || r.IsNull() || r.IsUndefined()) { _children.Add(n); return n; }
                    var idx = _children.IndexOf(r);
                    if (idx >= 0) _children.Insert(idx, n); else _children.Add(n);
                    return n;
                })),
            "removeChild" => _removeChildFn ??= JsValue.FromObject(Engine,
                new Func<JsValue, JsValue>(c => { _children.Remove(c); return c; })),
            "hasChildNodes" => JsValue.FromObject(Engine, new Func<bool>(() => _children.Count > 0)),
            "querySelector" => JsValue.FromObject(Engine, new Func<string, JsValue>(_ => JsValue.Null)),
            "querySelectorAll" => JsValue.FromObject(Engine, new Func<string, JsValue>(_ => Engine.Evaluate("[]"))),
            _ => base.Get(property, receiver)
        };
    }

    private static string GetTextContent(JsValue child)
    {
        if (child is ElementShim es) return es.Get("textContent", es).AsString();
        if (child is TextNodeShim ts) return ts.Get("textContent", ts).AsString();
        return "";
    }
}

/// <summary>
/// Jint ObjectInstance that represents a CSS style declaration (element.style).
/// Intercepts ALL property gets/sets at the .NET level.
/// </summary>
internal sealed class CssStyleDeclaration : ObjectInstance
{
    private readonly IElement _el;
    private readonly DocumentShim _doc;

    internal CssStyleDeclaration(Engine engine, IElement el, DocumentShim doc) : base(engine)
    {
        _el = el;
        _doc = doc;
    }

    private bool _inGetOwnProp;
    public override PropertyDescriptor GetOwnProperty(JsValue property)
    {
        if (_inGetOwnProp) return base.GetOwnProperty(property);
        _inGetOwnProp = true;
        try
        {
            var v = Get(property, this);
            return !v.IsUndefined() ? new PropertyDescriptor(v, true, true, true) : base.GetOwnProperty(property);
        }
        finally { _inGetOwnProp = false; }
    }

    public override JsValue Get(JsValue property, JsValue receiver)
    {
        if (!property.IsString()) return base.Get(property, receiver);
        return property.AsString() switch
        {
            "getPropertyValue" => JsValue.FromObject(Engine, new Func<string, string>(n => GetStyle(n) ?? "")),
            "setProperty" => JsValue.FromObject(Engine, new Action<string, string>((n, v) => SetStyle(n, v))),
            "removeProperty" => JsValue.FromObject(Engine, new Func<string, string>(n => { var old = GetStyle(n) ?? ""; SetStyle(n, ""); return old; })),
            "getPropertyPriority" => JsValue.FromObject(Engine, new Func<string, string>(_ => "")),
            "cssText" => (JsValue)GetCssText(),
            "length" => (JsValue)CountProperties(),
            "item" => JsValue.FromObject(Engine, new Func<int, string>(GetPropertyByIndex)),
            var name => (JsValue)(GetStyle(ToCss(name)) ?? ""),
        };
    }

    public override bool Set(JsValue property, JsValue value, JsValue receiver)
    {
        if (!property.IsString()) return base.Set(property, value, receiver);
        var name = property.AsString();
        if (name == "cssText") { SetCssText(value.ToString()); return true; }
        var strVal = value.IsNull() || value.IsUndefined() ? "" : value.ToString();
        SetStyle(ToCss(name), strVal);
        return true;
    }

    private string? GetStyle(string cssName)
    {
        var attr = _el.GetAttribute("style") ?? "";
        foreach (var decl in attr.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = decl.IndexOf(':');
            if (colon > 0 && decl[..colon].Trim().Equals(cssName, StringComparison.OrdinalIgnoreCase))
                return decl[(colon + 1)..].Trim();
        }
        return null;
    }

    private void SetStyle(string cssName, string value)
    {
        var attr = _el.GetAttribute("style") ?? "";
        var props = attr.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => d.Trim())
            .Where(d => !string.IsNullOrEmpty(d) &&
                        !d.StartsWith(cssName + ":", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (!string.IsNullOrEmpty(value))
            props.Add($"{cssName}: {value}");
        _el.SetAttribute("style", string.Join("; ", props));
        _doc.NotifyMutation();
    }

    private string GetCssText() => _el.GetAttribute("style") ?? "";

    private void SetCssText(string cssText)
    {
        _el.SetAttribute("style", cssText);
        _doc.NotifyMutation();
    }

    private int CountProperties()
    {
        var attr = _el.GetAttribute("style") ?? "";
        return attr.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Count(d => d.Contains(':'));
    }

    private string GetPropertyByIndex(int index)
    {
        var attr = _el.GetAttribute("style") ?? "";
        var props = attr.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Where(d => d.Contains(':'))
            .ToArray();
        if (index < 0 || index >= props.Length) return "";
        var colon = props[index].IndexOf(':');
        return colon > 0 ? props[index][..colon].Trim() : "";
    }

    private static string ToCss(string camel) =>
        System.Text.RegularExpressions.Regex.Replace(camel, "[A-Z]", m => "-" + m.Value.ToLower());
}

/// <summary>Wraps an AngleSharp IComment node as a JS comment node (nodeType=8).</summary>
public sealed class CommentShim : ObjectInstance
{
    private readonly ICharacterData _node;
    private readonly DocumentShim _doc;
    private readonly Dictionary<string, JsValue> _extraProps = new();

    internal INode NativeNode => _node;

    internal CommentShim(ICharacterData node, DocumentShim doc, Engine engine) : base(engine)
    {
        _node = node;
        _doc = doc;
    }

    private bool _inGetOwnProp;
    public override PropertyDescriptor GetOwnProperty(JsValue property)
    {
        if (_inGetOwnProp) return base.GetOwnProperty(property);
        _inGetOwnProp = true;
        try
        {
            var v = Get(property, this);
            return !v.IsUndefined() ? new PropertyDescriptor(v, true, true, true) : base.GetOwnProperty(property);
        }
        finally { _inGetOwnProp = false; }
    }

    public override JsValue Get(JsValue property, JsValue receiver) =>
        property.IsString() ? property.AsString() switch
        {
            "nodeType" => (JsValue)8,
            "nodeName" => (JsValue)"#comment",
            "textContent" or "data" or "nodeValue"
                          => (JsValue)(_node.Data ?? ""),
            "parentNode" => _node.ParentElement != null
                             ? _doc.WrapJsValue(_node.ParentElement)
                             : JsValue.Null,
            "parentElement" => JsValue.Null,
            "nextSibling" => _doc.WrapNode(_node.NextSibling),
            "previousSibling" => _doc.WrapNode(_node.PreviousSibling),
            "remove" => JsValue.FromObject(Engine, new Action(() => { _node.Remove(); _doc.NotifyMutation(); })),
            _ => _extraProps.TryGetValue(property.AsString(), out var v) ? v : base.Get(property, receiver)
        } : base.Get(property, receiver);

    public override bool Set(JsValue property, JsValue value, JsValue receiver)
    {
        if (property.IsString())
        {
            if (property.AsString() is "textContent" or "data" or "nodeValue")
            {
                _node.Data = value.ToString();
                return true;
            }
            _extraProps[property.AsString()] = value;
            return true;
        }
        return base.Set(property, value, receiver);
    }
}

/// <summary>Wraps an AngleSharp IText node as a JS text node (nodeType=3).</summary>
public sealed class TextNodeShim : ObjectInstance
{
    private readonly INode _node;
    private readonly DocumentShim _doc;
    private readonly Dictionary<string, JsValue> _extraProps = new();

    internal INode NativeNode => _node;

    internal TextNodeShim(INode node, DocumentShim doc, Engine engine) : base(engine)
    {
        _node = node;
        _doc = doc;
    }

    private bool _inGetOwnProp;
    public override PropertyDescriptor GetOwnProperty(JsValue property)
    {
        if (_inGetOwnProp) return base.GetOwnProperty(property);
        _inGetOwnProp = true;
        try
        {
            var v = Get(property, this);
            return !v.IsUndefined() ? new PropertyDescriptor(v, true, true, true) : base.GetOwnProperty(property);
        }
        finally { _inGetOwnProp = false; }
    }

    public override JsValue Get(JsValue property, JsValue receiver) =>
        property.IsString() ? property.AsString() switch
        {
            "nodeType" => (JsValue)3,
            "nodeName" => (JsValue)"#text",
            "textContent" or "nodeValue" or "data"
                          => (JsValue)(_node.TextContent ?? ""),
            "parentNode" => _node.ParentElement != null
                             ? _doc.WrapJsValue(_node.ParentElement)
                             : JsValue.Null,
            "parentElement" => _node.ParentElement != null
                             ? _doc.WrapJsValue(_node.ParentElement)
                             : JsValue.Null,
            "nextSibling" => _doc.WrapNode(_node.NextSibling),
            "previousSibling" => _doc.WrapNode(_node.PreviousSibling),
            "length" => (JsValue)(_node.TextContent?.Length ?? 0),
            _ => _extraProps.TryGetValue(property.AsString(), out var v) ? v : base.Get(property, receiver)
        } : base.Get(property, receiver);

    public override bool Set(JsValue property, JsValue value, JsValue receiver)
    {
        if (property.IsString())
        {
            if (property.AsString() is "textContent" or "nodeValue" or "data")
            {
                _node.TextContent = value.ToString();
                _doc.NotifyMutation();
                return true;
            }
            _extraProps[property.AsString()] = value;
            return true;
        }
        return base.Set(property, value, receiver);
    }
}

/// <summary>element.classList as an ObjectInstance with proper DOM API.</summary>
public sealed class ClassListShim : ObjectInstance
{
    private readonly IElement _el;
    private readonly DocumentShim _doc;

    private JsValue? _addFn, _removeFn, _toggleFn, _containsFn, _replaceFn, _itemFn;

    internal ClassListShim(IElement el, DocumentShim doc, Engine engine) : base(engine)
    {
        _el = el;
        _doc = doc;
    }

    private bool _inGetOwnProp;
    public override PropertyDescriptor GetOwnProperty(JsValue property)
    {
        if (_inGetOwnProp) return base.GetOwnProperty(property);
        _inGetOwnProp = true;
        try
        {
            var v = Get(property, this);
            return !v.IsUndefined() ? new PropertyDescriptor(v, true, true, true) : base.GetOwnProperty(property);
        }
        finally { _inGetOwnProp = false; }
    }

    private HashSet<string> GetClasses() =>
        new((_el.ClassName ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private void SetClasses(IEnumerable<string> classes)
    {
        _el.ClassName = string.Join(' ', classes);
        _doc.NotifyMutation();
    }

    public override JsValue Get(JsValue property, JsValue receiver)
    {
        if (!property.IsString()) return base.Get(property, receiver);
        return property.AsString() switch
        {
            "add" => _addFn ??= JsValue.FromObject(Engine,
                new Action<string>(cls => { var c = GetClasses(); c.Add(cls); SetClasses(c); })),
            "remove" => _removeFn ??= JsValue.FromObject(Engine,
                new Action<string>(cls => { var c = GetClasses(); c.Remove(cls); SetClasses(c); })),
            "toggle" => _toggleFn ??= JsValue.FromObject(Engine,
                new Func<string, bool>(cls =>
                {
                    var c = GetClasses();
                    if (c.Contains(cls)) { c.Remove(cls); SetClasses(c); return false; }
                    else { c.Add(cls); SetClasses(c); return true; }
                })),
            "contains" => _containsFn ??= JsValue.FromObject(Engine,
                new Func<string, bool>(cls => GetClasses().Contains(cls))),
            "replace" => _replaceFn ??= JsValue.FromObject(Engine,
                new Func<string, string, bool>((old, @new) =>
                {
                    var c = GetClasses();
                    if (!c.Remove(old)) return false;
                    c.Add(@new); SetClasses(c); return true;
                })),
            "item" => _itemFn ??= JsValue.FromObject(Engine,
                new Func<int, JsValue>(i =>
                {
                    var arr = GetClasses().ToArray();
                    return i >= 0 && i < arr.Length ? (JsValue)arr[i] : JsValue.Null;
                })),
            "value" => (JsValue)(_el.ClassName ?? ""),
            "length" => (JsValue)GetClasses().Count,
            _ => base.Get(property, receiver)
        };
    }

    public override bool Set(JsValue property, JsValue value, JsValue receiver)
    {
        if (property.IsString() && property.AsString() == "value")
        {
            _el.ClassName = value.ToString();
            _doc.NotifyMutation();
            return true;
        }
        return base.Set(property, value, receiver);
    }
}
