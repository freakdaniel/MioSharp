using AngleSharp.Dom;
using Jint;
using Jint.Native;

namespace Mio.Script;

/// <summary>
/// Exposes an AngleSharp IDocument to Jint as a JS document object.
/// Implements the minimal DOM API required by React/Vue.
/// </summary>
public sealed class DocumentShim
{
    private readonly IDocument _doc;
    private readonly Engine _engine;

    public event Action? DomMutated;

    public DocumentShim(IDocument doc, Engine engine)
    {
        _doc = doc;
        _engine = engine;
    }

    public ElementShim? getElementById(string id)
    {
        var el = _doc.GetElementById(id);
        return el != null ? Wrap(el) : null;
    }

    public ElementShim? querySelector(string selector)
    {
        try { var el = _doc.QuerySelector(selector); return el != null ? Wrap(el) : null; }
        catch { return null; }
    }

    public JsValue querySelectorAll(string selector)
    {
        try
        {
            var elements = _doc.QuerySelectorAll(selector);
            var arr = elements.Select(e => JsValue.FromObject(_engine, Wrap(e))).ToArray();
            return _engine.Evaluate("[]"); // simplified; full array wrapping via future phase
        }
        catch { return _engine.Evaluate("[]"); }
    }

    public ElementShim createElement(string tagName)
    {
        var el = _doc.CreateElement(tagName.ToLowerInvariant());
        NotifyMutation();
        return Wrap(el);
    }

    public ElementShim createTextNode(string text)
    {
        var el = _doc.CreateElement("span");
        el.TextContent = text;
        return Wrap(el);
    }

    public ElementShim? get_body() => _doc.Body != null ? Wrap(_doc.Body) : null;
    public ElementShim? get_documentElement() => _doc.DocumentElement != null ? Wrap(_doc.DocumentElement) : null;
    public string get_title() => _doc.Title ?? "";
    public void set_title(string v) { _doc.Title = v; }

    internal ElementShim Wrap(IElement el) => new(el, this, _engine);
    internal void NotifyMutation() => DomMutated?.Invoke();
}

/// <summary>Wraps an AngleSharp IElement for JS interop.</summary>
public sealed class ElementShim
{
    private readonly IElement _el;
    private readonly DocumentShim _doc;
    private readonly Engine _engine;
    private readonly Dictionary<string, List<JsValue>> _listeners = [];

    internal IElement NativeElement => _el;

    internal ElementShim(IElement element, DocumentShim doc, Engine engine)
    {
        _el = element;
        _doc = doc;
        _engine = engine;
    }

    // Properties
    public string get_tagName() => _el.TagName.ToLowerInvariant();
    public string get_id() => _el.Id ?? "";
    public void set_id(string v) { _el.Id = v; _doc.NotifyMutation(); }
    public string get_className() => _el.ClassName ?? "";
    public void set_className(string v) { _el.ClassName = v; _doc.NotifyMutation(); }
    public string? get_innerHTML() => _el.InnerHtml;
    public void set_innerHTML(string v) { _el.InnerHtml = v; _doc.NotifyMutation(); }
    public string? get_textContent() => _el.TextContent;
    public void set_textContent(string v) { _el.TextContent = v; _doc.NotifyMutation(); }
    public string? get_innerText() => _el.TextContent;
    public void set_innerText(string v) { _el.TextContent = v; _doc.NotifyMutation(); }
    public string? get_value() => _el.GetAttribute("value") ?? "";
    public void set_value(string v) { _el.SetAttribute("value", v); _doc.NotifyMutation(); }

    // Style proxy
    public StyleProxy get_style() => new(_el, _doc);

    // Attributes
    public string? getAttribute(string name) => _el.GetAttribute(name);
    public void setAttribute(string name, string value) { _el.SetAttribute(name, value); _doc.NotifyMutation(); }
    public bool hasAttribute(string name) => _el.HasAttribute(name);
    public void removeAttribute(string name) { _el.RemoveAttribute(name); _doc.NotifyMutation(); }

    // DOM tree
    public ElementShim appendChild(ElementShim child)
    {
        _el.AppendChild(child._el);
        _doc.NotifyMutation();
        return child;
    }

    public ElementShim? removeChild(ElementShim child)
    {
        _el.RemoveChild(child._el);
        _doc.NotifyMutation();
        return child;
    }

    public void replaceChild(ElementShim newChild, ElementShim oldChild)
    {
        _el.ReplaceChild(newChild._el, oldChild._el);
        _doc.NotifyMutation();
    }

    public void insertBefore(ElementShim newChild, ElementShim? refChild)
    {
        if (refChild != null)
            _el.InsertBefore(newChild._el, refChild._el);
        else
            _el.AppendChild(newChild._el);
        _doc.NotifyMutation();
    }

    public ElementShim? get_parentElement()
    {
        var p = _el.ParentElement;
        return p != null ? new ElementShim(p, _doc, _engine) : null;
    }

    public ElementShim? get_firstChild() =>
        _el.FirstElementChild != null ? new(_el.FirstElementChild, _doc, _engine) : null;

    public ElementShim? get_nextSibling()
    {
        var ns = _el.NextElementSibling;
        return ns != null ? new(ns, _doc, _engine) : null;
    }

    public JsValue get_children()
    {
        var arr = _el.Children.Select(c => JsValue.FromObject(_engine, new ElementShim(c, _doc, _engine))).ToArray();
        return _engine.Evaluate($"[]"); // simplified
    }

    public bool get_isConnected() => _el.ParentElement != null;

    // Events
    public void addEventListener(string type, JsValue handler)
    {
        if (!_listeners.TryGetValue(type, out var list))
            _listeners[type] = list = [];
        list.Add(handler);
    }

    public void removeEventListener(string type, JsValue handler)
    {
        if (_listeners.TryGetValue(type, out var list))
            list.Remove(handler);
    }

    public bool dispatchEvent(JsValue eventObj)
    {
        var type = eventObj.AsObject()["type"].AsString();
        FireEvent(type, eventObj);
        return true;
    }

    internal void FireEvent(string type, JsValue? eventArg = null)
    {
        if (!_listeners.TryGetValue(type, out var list)) return;
        var arg = eventArg ?? _engine.Evaluate($"({{ type: '{type}', preventDefault: function(){{}}, stopPropagation: function(){{}} }})");
        foreach (var fn in list.ToList())
        {
            try { _engine.Invoke(fn, JsValue.Undefined, [arg]); }
            catch (Exception ex) { Console.Error.WriteLine($"[Event:{type}] {ex.Message}"); }
        }
    }

    // Class list
    public ClassListShim get_classList() => new(_el, _doc);

    // Geometry (approximate)
    public JsValue getBoundingClientRect() =>
        _engine.Evaluate("({ left:0, top:0, right:0, bottom:0, width:0, height:0 })");

    // Query
    public ElementShim? querySelector(string selector)
    {
        try { var el = _el.QuerySelector(selector); return el != null ? new(el, _doc, _engine) : null; }
        catch { return null; }
    }
}

/// <summary>Allows JS: element.style.color = 'red'</summary>
public sealed class StyleProxy
{
    private readonly IElement _el;
    private readonly DocumentShim _doc;

    public StyleProxy(IElement el, DocumentShim doc) { _el = el; _doc = doc; }

    public string? getPropertyValue(string name) => GetStyle(name);
    public void setProperty(string name, string value) => SetStyle(name, value);
    public void removeProperty(string name) => SetStyle(name, "");

    private string? GetStyle(string name)
    {
        var style = _el.GetAttribute("style") ?? "";
        foreach (var decl in style.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = decl.IndexOf(':');
            if (colon > 0 && decl[..colon].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                return decl[(colon + 1)..].Trim();
        }
        return null;
    }

    private void SetStyle(string name, string value)
    {
        var style = _el.GetAttribute("style") ?? "";
        var props = style.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => d.Trim())
            .Where(d => !string.IsNullOrEmpty(d) && !d.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (!string.IsNullOrEmpty(value))
            props.Add($"{name}: {value}");
        _el.SetAttribute("style", string.Join("; ", props));
        _doc.NotifyMutation();
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
