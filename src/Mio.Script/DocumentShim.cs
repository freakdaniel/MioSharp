using AngleSharp.Dom;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime.Descriptors;

namespace Mio.Script;

/// <summary>
/// Exposes an AngleSharp IDocument to Jint as a JS document object.
/// Implements the minimal DOM API required by React/Vue.
///
/// IMPORTANT: All methods that return DOM elements MUST return a cached JsValue via WrapJsValue().
/// Jint creates a new ObjectWrapper on every JsValue.FromObject() call, even for the same C# object.
/// Vue stores internal state (_vnode, _vei, __vue_app__) as JS properties on DOM elements.
/// If two different JsValues are returned for the same IElement, one will silently lose that state,
/// causing Vue to re-mount instead of patch → duplicate content.
/// </summary>
public sealed partial class DocumentShim
{
    private readonly IDocument _doc;
    private readonly Engine _engine;

    /// <summary>
    /// Stable JsValue cache keyed by IElement identity.
    /// The SAME JsValue (ObjectWrapper) is always returned for the same DOM element.
    /// This guarantees that JS properties set on an element (Vue's _vnode, _vei, etc.)
    /// are visible on every subsequent access to that element.
    /// </summary>
    private readonly Dictionary<IElement, JsValue> _jsValueCache = [];

    /// <summary>
    /// Stable ElementShim cache — for internal C# callers that need the CLR object.
    /// Kept in sync with _jsValueCache.
    /// </summary>
    private readonly Dictionary<IElement, ElementShim> _shimCache = [];

    /// <summary>Shared listener storage keyed by IElement.</summary>
    internal readonly Dictionary<IElement, Dictionary<string, List<JsValue>>> Listeners = [];

    internal Dictionary<string, List<JsValue>> GetListeners(IElement el)
    {
        if (!Listeners.TryGetValue(el, out var dict))
            Listeners[el] = dict = [];
        return dict;
    }

    public event Action? DomMutated;

    public DocumentShim(IDocument doc, Engine engine)
    {
        _doc = doc;
        _engine = engine;
    }

    // ── Cache helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the canonical (cached) ElementShim for this IElement.
    /// Internal use only — for JS-facing APIs use WrapJsValue().
    /// </summary>
    internal ElementShim Wrap(IElement el)
    {
        if (!_shimCache.TryGetValue(el, out var shim))
        {
            shim = new ElementShim(el, this, _engine);
            _shimCache[el] = shim;
            _jsValueCache[el] = JsValue.FromObject(_engine, shim);
        }
        return shim;
    }

    /// <summary>
    /// Returns the canonical (cached) JsValue for this IElement.
    /// MUST be used everywhere a DOM element is returned to JS.
    /// </summary>
    internal JsValue WrapJsValue(IElement el)
    {
        if (!_jsValueCache.TryGetValue(el, out var v))
        {
            var shim = new ElementShim(el, this, _engine);
            _shimCache[el] = shim;
            _jsValueCache[el] = v = JsValue.FromObject(_engine, shim);
        }
        return v;
    }

    internal void NotifyMutation() => DomMutated?.Invoke();

    /// <summary>Wraps any INode as the correct shim type, always using the JsValue cache for elements.</summary>
    internal JsValue WrapNode(INode? node)
    {
        if (node == null) return JsValue.Null;
        return node switch
        {
            IElement el => WrapJsValue(el),
            IComment co => JsValue.FromObject(_engine, new CommentShim(co, this, _engine)),
            IText    tx => JsValue.FromObject(_engine, new TextNodeShim(tx, this, _engine)),
            _           => JsValue.Null,
        };
    }

    /// <summary>Builds a proper JS Array from a sequence of JsValues using JS's native push.</summary>
    internal JsValue BuildJsArray(IEnumerable<JsValue> items)
    {
        var list = items.ToList();
        if (list.Count == 0) return _engine.Evaluate("[]");
        _engine.SetValue("__arrBld__", new Func<int, JsValue>(i => i < list.Count ? list[i] : JsValue.Undefined));
        var result = _engine.Evaluate($"(function(){{var a=[];for(var i=0;i<{list.Count};i++)a.push(__arrBld__(i));return a;}})()");
        _engine.SetValue("__arrBld__", JsValue.Undefined);
        return result;
    }

    // ── Document properties ────────────────────────────────────────────────────

    /// <summary>Returns the cached JsValue for &lt;body&gt;. Jint uses it as-is, preserving Vue state.</summary>
    public JsValue body => _doc.Body != null ? WrapJsValue(_doc.Body) : JsValue.Null;
    public JsValue head => _doc.Head != null ? WrapJsValue(_doc.Head) : JsValue.Null;
    public JsValue documentElement => _doc.DocumentElement != null ? WrapJsValue(_doc.DocumentElement) : JsValue.Null;

    public string title
    {
        get => _doc.Title ?? "";
        set => _doc.Title = value;
    }

    // ── Document methods ───────────────────────────────────────────────────────

    public JsValue getElementById(string id)
    {
        var el = _doc.GetElementById(id);
        return el != null ? WrapJsValue(el) : JsValue.Null;
    }

    public JsValue querySelector(string selector)
    {
        try
        {
            var el = _doc.QuerySelector(selector);
            return el != null ? WrapJsValue(el) : JsValue.Null;
        }
        catch { return JsValue.Null; }
    }

    public JsValue querySelectorAll(string selector)
    {
        try
        {
            var elements = _doc.QuerySelectorAll(selector);
            return BuildJsArray(elements.Select(e => WrapJsValue(e)));
        }
        catch { return _engine.Evaluate("[]"); }
    }

    public JsValue createElement(string tagName)
    {
        var el = _doc.CreateElement(tagName.ToLowerInvariant());
        NotifyMutation();
        return WrapJsValue(el);
    }

    /// <summary>createElement(tagName, options) — 2-arg variant used by Vue for custom elements ({is:...}).</summary>
    public JsValue createElement(string tagName, JsValue options) => createElement(tagName);

    /// <summary>
    /// createElementNS — used by Vue/React for SVG and MathML elements.
    /// Stores the namespace as a data attribute so the layout engine can detect SVG.
    /// </summary>
    public JsValue createElementNS(string? ns, string tagName)
    {
        var el = _doc.CreateElement(tagName.ToLowerInvariant());
        if (!string.IsNullOrEmpty(ns))
            el.SetAttribute("data-ns", ns);
        NotifyMutation();
        return WrapJsValue(el);
    }

    /// <summary>Creates a real IText node wrapped in TextNodeShim (nodeType=3).</summary>
    public JsValue createTextNode(string text)
    {
        var textNode = _doc.CreateTextNode(text);
        return JsValue.FromObject(_engine, new TextNodeShim(textNode, this, _engine));
    }

    public JsValue createComment(string text)
    {
        var comment = _doc.CreateComment(text);
        return JsValue.FromObject(_engine, new CommentShim(comment, this, _engine));
    }

    public JsValue createDocumentFragment()
    {
        return _engine.Evaluate(@"({
            nodeType: 11,
            children: [],
            appendChild: function(c) { this.children.push(c); return c; },
            childNodes: [],
            firstChild: null
        })");
    }
}

/// <summary>Wraps an AngleSharp IElement for JS interop.</summary>
public sealed class ElementShim
{
    private readonly IElement _el;
    private readonly DocumentShim _doc;
    private readonly Engine _engine;

    private Dictionary<string, List<JsValue>> Listeners => _doc.GetListeners(_el);

    internal IElement NativeElement => _el;

    internal ElementShim(IElement element, DocumentShim doc, Engine engine)
    {
        _el = element;
        _doc = doc;
        _engine = engine;
    }

    public string tagName => _el.TagName.ToLowerInvariant();
    public string nodeName => _el.TagName.ToLowerInvariant();
    public int nodeType => 1;

    public string id
    {
        get => _el.Id ?? "";
        set { _el.Id = value; _doc.NotifyMutation(); }
    }

    public string className
    {
        get => _el.ClassName ?? "";
        set { _el.ClassName = value; _doc.NotifyMutation(); }
    }

    public string? innerHTML
    {
        get => _el.InnerHtml;
        set { _el.InnerHtml = value ?? ""; _doc.NotifyMutation(); }
    }

    public string? textContent
    {
        get => _el.TextContent;
        set { _el.TextContent = value ?? ""; _doc.NotifyMutation(); }
    }

    public string? innerText
    {
        get => _el.TextContent;
        set { _el.TextContent = value ?? ""; _doc.NotifyMutation(); }
    }

    public string? value
    {
        get => _el.GetAttribute("value") ?? "";
        set { _el.SetAttribute("value", value ?? ""); _doc.NotifyMutation(); }
    }

    public JsValue style => new CssStyleDeclaration(_engine, _el, _doc);

    /// <summary>Returns the cached JsValue for the parent element (preserves Vue state on parent).</summary>
    public JsValue parentElement
    {
        get
        {
            var p = _el.ParentElement;
            return p != null ? _doc.WrapJsValue(p) : JsValue.Null;
        }
    }

    public JsValue firstChild  => _doc.WrapNode(_el.FirstChild);
    public JsValue lastChild   => _doc.WrapNode(_el.LastChild);
    public JsValue nextSibling => _doc.WrapNode(_el.NextSibling);
    public JsValue previousSibling => _doc.WrapNode(_el.PreviousSibling);

    public JsValue children =>
        _doc.BuildJsArray(_el.Children.Select(c => _doc.WrapJsValue(c)));

    public JsValue childNodes =>
        _doc.BuildJsArray(_el.ChildNodes.Select(n => _doc.WrapNode(n)));

    public JsValue parentNode
    {
        get
        {
            var p = _el.ParentElement;
            return p != null ? _doc.WrapJsValue(p) : JsValue.Null;
        }
    }

    public bool isConnected => _el.ParentElement != null;

    public ClassListShim classList => new(_el, _doc);

    public string? getAttribute(string name) => _el.GetAttribute(name);
    public void setAttribute(string name, string value) { _el.SetAttribute(name, value); _doc.NotifyMutation(); }
    public bool hasAttribute(string name) => _el.HasAttribute(name);
    public void removeAttribute(string name) { _el.RemoveAttribute(name); _doc.NotifyMutation(); }

    public JsValue appendChild(JsValue child)
    {
        var node = DocumentShim.ExtractNode(child);
        if (node != null) { _el.AppendChild(node); _doc.NotifyMutation(); }
        return child;
    }

    public JsValue removeChild(JsValue child)
    {
        var node = DocumentShim.ExtractNode(child);
        if (node != null) { _el.RemoveChild(node); _doc.NotifyMutation(); }
        return child;
    }

    public void replaceChild(JsValue newChild, JsValue oldChild)
    {
        var newNode = DocumentShim.ExtractNode(newChild);
        var oldNode = DocumentShim.ExtractNode(oldChild);
        if (newNode != null && oldNode != null) { _el.ReplaceChild(newNode, oldNode); _doc.NotifyMutation(); }
    }

    public void insertBefore(JsValue newChild, JsValue refChild)
    {
        var newNode = DocumentShim.ExtractNode(newChild);
        if (newNode == null) return;
        var refNode = DocumentShim.ExtractNode(refChild);
        if (refNode != null) _el.InsertBefore(newNode, refNode);
        else _el.AppendChild(newNode);
        _doc.NotifyMutation();
    }

    public void addEventListener(string type, JsValue handler)
    {
        var listeners = Listeners;
        if (!listeners.TryGetValue(type, out var list)) listeners[type] = list = [];
        list.Add(handler);
    }

    public void addEventListener(string type, JsValue handler, JsValue options) =>
        addEventListener(type, handler);

    public void removeEventListener(string type, JsValue handler)
    {
        if (Listeners.TryGetValue(type, out var list)) list.Remove(handler);
    }

    public void removeEventListener(string type, JsValue handler, JsValue options) =>
        removeEventListener(type, handler);

    public bool dispatchEvent(JsValue eventObj)
    {
        var type = eventObj.AsObject()["type"].AsString();
        FireEvent(type, eventObj);
        return true;
    }

    internal void FireEvent(string type, JsValue? eventArg = null)
    {
        var arg = eventArg ?? _engine.Evaluate($"({{ type: '{type}', target: null, preventDefault: function(){{}}, stopPropagation: function(){{}} }})");

        var attrHandler = _el.GetAttribute("on" + type);
        if (!string.IsNullOrEmpty(attrHandler))
        {
            try { _engine.Execute(attrHandler); }
            catch (Exception ex) { Console.Error.WriteLine($"[Event:{type} inline] {ex.Message}"); }
        }

        if (!Listeners.TryGetValue(type, out var list)) return;
        foreach (var fn in list.ToList())
        {
            try { _engine.Invoke(fn, JsValue.Undefined, [arg]); }
            catch (Exception ex) { Console.Error.WriteLine($"[Event:{type}] {ex.Message}"); }
        }
    }

    public JsValue getBoundingClientRect() =>
        _engine.Evaluate("({ left:0, top:0, right:0, bottom:0, width:0, height:0 })");

    public JsValue getRootNode() => JsValue.Null;

    public JsValue querySelector(string selector)
    {
        try
        {
            var el = _el.QuerySelector(selector);
            return el != null ? _doc.WrapJsValue(el) : JsValue.Null;
        }
        catch { return JsValue.Null; }
    }

    public JsValue querySelectorAll(string selector)
    {
        try
        {
            var elements = _el.QuerySelectorAll(selector);
            return _doc.BuildJsArray(elements.Select(e => _doc.WrapJsValue(e)));
        }
        catch { return _engine.Evaluate("[]"); }
    }

    public JsValue cloneNode(bool deep = false)
    {
        try
        {
            var clone = (IElement)_el.Clone(deep);
            return _doc.WrapJsValue(clone);
        }
        catch { return JsValue.Null; }
    }

    public void remove()
    {
        _el.Remove();
        _doc.NotifyMutation();
    }

    public bool contains(JsValue other)
    {
        try
        {
            if (other.IsNull() || other.IsUndefined()) return false;
            var underlying = other.ToObject();
            if (underlying is ElementShim shim) return _el.Contains(shim.NativeElement);
            return false;
        }
        catch { return false; }
    }

    /// <summary>
    /// Extracts the underlying AngleSharp INode from any node shim.
    /// Static so it can be called from any shim without a DocumentShim reference.
    /// </summary>
    internal static INode? ExtractNode(JsValue v)
    {
        if (v.IsNull() || v.IsUndefined()) return null;
        return v.ToObject() switch
        {
            ElementShim  es => es.NativeElement,
            CommentShim  cs => cs.NativeNode,
            TextNodeShim ts => ts.NativeNode,
            _               => null,
        };
    }
}

// Keep DocumentShim.ExtractNode as a forwarding alias for backward compat with CommentShim/TextNodeShim
public sealed partial class DocumentShim
{
    internal static INode? ExtractNode(JsValue v) => ElementShim.ExtractNode(v);
}

/// <summary>
/// Jint ObjectInstance that represents a CSS style declaration (element.style).
/// Intercepts ALL property gets/sets at the .NET level — no JS Proxy needed.
/// </summary>
internal sealed class CssStyleDeclaration : ObjectInstance
{
    private readonly IElement _el;
    private readonly DocumentShim _doc;
    private readonly Engine _jint;

    internal CssStyleDeclaration(Engine engine, IElement el, DocumentShim doc) : base(engine)
    {
        _el = el;
        _doc = doc;
        _jint = engine;
    }

    public override JsValue Get(JsValue property, JsValue receiver)
    {
        if (!property.IsString()) return base.Get(property, receiver);
        return property.AsString() switch
        {
            "getPropertyValue" => JsValue.FromObject(_jint, new Func<string, string>(n => GetStyle(n) ?? "")),
            "setProperty"      => JsValue.FromObject(_jint, new Action<string, string>((n, v) => SetStyle(n, v))),
            "removeProperty"   => JsValue.FromObject(_jint, new Action<string>(n => SetStyle(n, ""))),
            "cssText"          => JsValue.FromObject(_jint, GetCssText()),
            var name           => JsValue.FromObject(_jint, GetStyle(ToCss(name)) ?? ""),
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

    private static string ToCss(string camel) =>
        System.Text.RegularExpressions.Regex.Replace(camel, "[A-Z]", m => "-" + m.Value.ToLower());
}

/// <summary>Wraps an AngleSharp IComment node as a JS comment node (nodeType=8).</summary>
public sealed class CommentShim : ObjectInstance
{
    private readonly ICharacterData _node;
    private readonly DocumentShim _doc;

    internal INode NativeNode => _node;

    internal CommentShim(ICharacterData node, DocumentShim doc, Engine engine) : base(engine)
    {
        _node = node;
        _doc = doc;
    }

    public override JsValue Get(JsValue property, JsValue receiver) =>
        property.IsString() ? property.AsString() switch
        {
            "nodeType"    => JsValue.FromObject(Engine, 8),
            "nodeName"    => JsValue.FromObject(Engine, "#comment"),
            "textContent" or "data" or "nodeValue"
                          => JsValue.FromObject(Engine, _node.Data ?? ""),
            "parentNode"  => _node.ParentElement != null
                             ? _doc.WrapJsValue(_node.ParentElement)
                             : JsValue.Null,
            "nextSibling"     => _doc.WrapNode(_node.NextSibling),
            "previousSibling" => _doc.WrapNode(_node.PreviousSibling),
            "remove"      => JsValue.FromObject(Engine, new Action(() => { _node.Remove(); _doc.NotifyMutation(); })),
            _             => base.Get(property, receiver)
        } : base.Get(property, receiver);

    public override bool Set(JsValue property, JsValue value, JsValue receiver)
    {
        if (property.IsString() && property.AsString() is "textContent" or "data" or "nodeValue")
        {
            _node.Data = value.ToString();
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

    internal INode NativeNode => _node;

    internal TextNodeShim(INode node, DocumentShim doc, Engine engine) : base(engine)
    {
        _node = node;
        _doc = doc;
    }

    public override JsValue Get(JsValue property, JsValue receiver) =>
        property.IsString() ? property.AsString() switch
        {
            "nodeType"    => JsValue.FromObject(Engine, 3),
            "nodeName"    => JsValue.FromObject(Engine, "#text"),
            "textContent" or "nodeValue" or "data"
                          => JsValue.FromObject(Engine, _node.TextContent ?? ""),
            "parentNode"  => _node.ParentElement != null
                             ? _doc.WrapJsValue(_node.ParentElement)
                             : JsValue.Null,
            "nextSibling"     => _doc.WrapNode(_node.NextSibling),
            "previousSibling" => _doc.WrapNode(_node.PreviousSibling),
            _             => base.Get(property, receiver)
        } : base.Get(property, receiver);

    public override bool Set(JsValue property, JsValue value, JsValue receiver)
    {
        if (property.IsString() && property.AsString() is "textContent" or "nodeValue" or "data")
        {
            _node.TextContent = value.ToString();
            _doc.NotifyMutation();
            return true;
        }
        return base.Set(property, value, receiver);
    }
}

/// <summary>Allows JS: element.classList.add/remove/toggle/contains</summary>
public sealed class ClassListShim
{
    private readonly IElement _el;
    private readonly DocumentShim _doc;

    public ClassListShim(IElement el, DocumentShim doc) { _el = el; _doc = doc; }

    private HashSet<string> GetClasses() =>
        new((_el.ClassName ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private void SetClasses(HashSet<string> classes)
    {
        _el.ClassName = string.Join(' ', classes);
        _doc.NotifyMutation();
    }

    public void add(string cls) { var c = GetClasses(); c.Add(cls); SetClasses(c); }
    public void remove(string cls) { var c = GetClasses(); c.Remove(cls); SetClasses(c); }
    public bool contains(string cls) => GetClasses().Contains(cls);
    public void toggle(string cls) { if (!contains(cls)) add(cls); else remove(cls); }
}
