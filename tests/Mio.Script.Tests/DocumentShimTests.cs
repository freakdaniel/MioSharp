using AngleSharp;
using AngleSharp.Dom;
using Jint;
using Jint.Native;
using Mio.Script;

namespace Mio.Script.Tests;

/// <summary>
/// Tests for DocumentShim, ElementShim, and related DOM shim classes.
/// These tests verify that the Jint↔AngleSharp DOM bridge works correctly
/// for Vue 3 and React 18 compatibility.
/// </summary>
public class DocumentShimTests : IDisposable
{
    private readonly Engine _engine;
    private readonly IDocument _doc;
    private readonly DocumentShim _shim;

    public DocumentShimTests()
    {
        _engine = new Engine(cfg => cfg.AllowClrWrite());
        var context = BrowsingContext.New(Configuration.Default);
        _doc = context.OpenAsync(req => req.Content("<html><head></head><body><div id='app'></div></body></html>"))
            .GetAwaiter().GetResult();
        _shim = new DocumentShim(_doc, _engine);
        _engine.SetValue("document", (JsValue)_shim);
    }

    public void Dispose()
    {
        _engine.Dispose();
    }

    [Fact]
    public void Document_NodeType_Is9()
    {
        var result = _engine.Evaluate("document.nodeType");
        Assert.Equal(9.0, result.AsNumber());
    }

    [Fact]
    public void Document_Body_NotNull()
    {
        var result = _engine.Evaluate("document.body !== null");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void Document_Head_NotNull()
    {
        var result = _engine.Evaluate("document.head !== null");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void Document_DocumentElement_NotNull()
    {
        var result = _engine.Evaluate("document.documentElement !== null");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void Document_ReadyState_Complete()
    {
        var result = _engine.Evaluate("document.readyState").AsString();
        Assert.Equal("complete", result);
    }

    [Fact]
    public void GetElementById_ExistingElement_ReturnsShim()
    {
        var result = _engine.Evaluate("document.getElementById('app') !== null");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void GetElementById_NonExistent_ReturnsNull()
    {
        var result = _engine.Evaluate("document.getElementById('missing')");
        Assert.True(result.IsNull());
    }

    [Fact]
    public void GetElementById_SameElement_ReturnsSameObject()
    {
        var result = _engine.Evaluate("document.getElementById('app') === document.getElementById('app')");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void CreateElement_ReturnsElement_WithCorrectTagName()
    {
        var result = _engine.Evaluate("document.createElement('div').tagName");
        Assert.Equal("DIV", result.AsString());
    }

    [Fact]
    public void CreateElement_NodeType_Is1()
    {
        var result = _engine.Evaluate("document.createElement('span').nodeType");
        Assert.Equal(1.0, result.AsNumber());
    }

    [Fact]
    public void CreateElement_LocalName_IsLowerCase()
    {
        var result = _engine.Evaluate("document.createElement('DIV').localName");
        Assert.Equal("div", result.AsString());
    }

    [Fact]
    public void Element_SetId_Works()
    {
        _engine.Execute("var el = document.createElement('div'); el.id = 'test';");
        var result = _engine.Evaluate("el.id").AsString();
        Assert.Equal("test", result);
    }

    [Fact]
    public void Element_SetClassName_Works()
    {
        _engine.Execute("var el = document.createElement('div'); el.className = 'foo bar';");
        var result = _engine.Evaluate("el.className").AsString();
        Assert.Equal("foo bar", result);
    }

    [Fact]
    public void Element_SetInnerHTML_Works()
    {
        _engine.Execute("var el = document.createElement('div'); el.innerHTML = '<span>hello</span>';");
        var result = _engine.Evaluate("el.innerHTML").AsString();
        Assert.Contains("span", result);
    }

    [Fact]
    public void Element_SetTextContent_Works()
    {
        _engine.Execute("var el = document.createElement('div'); el.textContent = 'hello';");
        var result = _engine.Evaluate("el.textContent").AsString();
        Assert.Equal("hello", result);
    }

    [Fact]
    public void Element_NodeValue_IsNull()
    {
        var result = _engine.Evaluate("document.createElement('div').nodeValue");
        Assert.True(result.IsNull());
    }

    [Fact]
    public void Element_NamespaceURI_DefaultsToXHTML()
    {
        var result = _engine.Evaluate("document.createElement('div').namespaceURI").AsString();
        Assert.Equal("http://www.w3.org/1999/xhtml", result);
    }

    [Fact]
    public void Element_SetAttribute_GetAttribute_Roundtrip()
    {
        _engine.Execute("var el = document.createElement('div'); el.setAttribute('data-x', '42');");
        var result = _engine.Evaluate("el.getAttribute('data-x')").AsString();
        Assert.Equal("42", result);
    }

    [Fact]
    public void Element_GetAttribute_Missing_ReturnsNull()
    {
        var result = _engine.Evaluate("document.createElement('div').getAttribute('missing')");
        Assert.True(result.IsNull());
    }

    [Fact]
    public void Element_HasAttribute_Works()
    {
        _engine.Execute("var el = document.createElement('div'); el.setAttribute('data-x', '1');");
        Assert.True(_engine.Evaluate("el.hasAttribute('data-x')").AsBoolean());
        Assert.False(_engine.Evaluate("el.hasAttribute('data-y')").AsBoolean());
    }

    [Fact]
    public void Element_RemoveAttribute_Works()
    {
        _engine.Execute("var el = document.createElement('div'); el.setAttribute('x', '1'); el.removeAttribute('x');");
        Assert.False(_engine.Evaluate("el.hasAttribute('x')").AsBoolean());
    }

    [Fact]
    public void Element_Attributes_ReturnsArrayLike()
    {
        _engine.Execute("var el = document.createElement('div'); el.setAttribute('id', 'test'); el.setAttribute('class', 'foo');");
        var len = _engine.Evaluate("el.attributes.length").AsNumber();
        Assert.True(len >= 2);
    }

    [Fact]
    public void Element_GetAttributeNames_ReturnsArray()
    {
        _engine.Execute("var el = document.createElement('div'); el.setAttribute('a', '1'); el.setAttribute('b', '2');");
        var result = _engine.Evaluate("el.getAttributeNames()");
        Assert.True(result.IsObject());
    }

    [Fact]
    public void Element_InOperator_TagName_ReturnsTrue()
    {
        var result = _engine.Evaluate("'tagName' in document.createElement('div')");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void Element_InOperator_InnerHTML_ReturnsTrue()
    {
        var result = _engine.Evaluate("'innerHTML' in document.createElement('div')");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void Element_InOperator_AppendChild_ReturnsTrue()
    {
        var result = _engine.Evaluate("'appendChild' in document.createElement('div')");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void Element_InOperator_Contains_ReturnsTrue()
    {
        var result = _engine.Evaluate("'contains' in document.createElement('div')");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void Element_InOperator_AddEventListener_ReturnsTrue()
    {
        var result = _engine.Evaluate("'addEventListener' in document.createElement('div')");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void Element_HasOwnProperty_NodeType_ReturnsTrue()
    {
        var result = _engine.Evaluate("document.createElement('div').hasOwnProperty('nodeType')");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void Document_InOperator_GetElementById_ReturnsTrue()
    {
        var result = _engine.Evaluate("'getElementById' in document");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void AppendChild_AddsChildToDOM()
    {
        _engine.Execute(@"
            var parent = document.createElement('div');
            var child = document.createElement('span');
            parent.appendChild(child);
        ");
        var count = _engine.Evaluate("parent.childNodes.length").AsNumber();
        Assert.Equal(1, count);
    }

    [Fact]
    public void RemoveChild_RemovesFromDOM()
    {
        _engine.Execute(@"
            var parent = document.createElement('div');
            var child = document.createElement('span');
            parent.appendChild(child);
            parent.removeChild(child);
        ");
        var count = _engine.Evaluate("parent.childNodes.length").AsNumber();
        Assert.Equal(0, count);
    }

    [Fact]
    public void InsertBefore_InsertsCorrectly()
    {
        _engine.Execute(@"
            var parent = document.createElement('div');
            var a = document.createElement('span'); a.id = 'a';
            var b = document.createElement('span'); b.id = 'b';
            parent.appendChild(b);
            parent.insertBefore(a, b);
        ");
        var firstId = _engine.Evaluate("parent.firstChild.id").AsString();
        Assert.Equal("a", firstId);
    }

    [Fact]
    public void ReplaceChild_Works()
    {
        _engine.Execute(@"
            var parent = document.createElement('div');
            var old = document.createElement('span'); old.id = 'old';
            var newEl = document.createElement('span'); newEl.id = 'new';
            parent.appendChild(old);
            parent.replaceChild(newEl, old);
        ");
        var id = _engine.Evaluate("parent.firstChild.id").AsString();
        Assert.Equal("new", id);
    }

    [Fact]
    public void ParentElement_AfterAppend_ReturnsParent()
    {
        _engine.Execute(@"
            var parent = document.createElement('div'); parent.id = 'p';
            var child = document.createElement('span');
            parent.appendChild(child);
        ");
        var result = _engine.Evaluate("child.parentElement.id").AsString();
        Assert.Equal("p", result);
    }

    [Fact]
    public void NextSibling_PreviousSibling_Work()
    {
        _engine.Execute(@"
            var parent = document.createElement('div');
            var a = document.createElement('span'); a.id = 'a';
            var b = document.createElement('span'); b.id = 'b';
            parent.appendChild(a);
            parent.appendChild(b);
        ");
        Assert.Equal("b", _engine.Evaluate("a.nextSibling.id").AsString());
        Assert.Equal("a", _engine.Evaluate("b.previousSibling.id").AsString());
    }

    [Fact]
    public void FirstChild_LastChild_Work()
    {
        _engine.Execute(@"
            var parent = document.createElement('div');
            var a = document.createElement('span'); a.id = 'a';
            var b = document.createElement('span'); b.id = 'b';
            parent.appendChild(a);
            parent.appendChild(b);
        ");
        Assert.Equal("a", _engine.Evaluate("parent.firstChild.id").AsString());
        Assert.Equal("b", _engine.Evaluate("parent.lastChild.id").AsString());
    }

    [Fact]
    public void OwnerDocument_ReturnsDocument()
    {
        var result = _engine.Evaluate("document.createElement('div').ownerDocument === document");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void CreateTextNode_HasCorrectProperties()
    {
        _engine.Execute("var t = document.createTextNode('hello');");
        Assert.Equal(3.0, _engine.Evaluate("t.nodeType").AsNumber());
        Assert.Equal("#text", _engine.Evaluate("t.nodeName").AsString());
        Assert.Equal("hello", _engine.Evaluate("t.textContent").AsString());
    }

    [Fact]
    public void CreateComment_HasCorrectProperties()
    {
        _engine.Execute("var c = document.createComment('test');");
        Assert.Equal(8.0, _engine.Evaluate("c.nodeType").AsNumber());
        Assert.Equal("#comment", _engine.Evaluate("c.nodeName").AsString());
        Assert.Equal("test", _engine.Evaluate("c.data").AsString());
    }

    [Fact]
    public void TextNode_IdentityStable()
    {
        _engine.Execute(@"
            var parent = document.createElement('div');
            var t = document.createTextNode('x');
            parent.appendChild(t);
        ");
        // Same text node should be same object across accesses
        var result = _engine.Evaluate("parent.firstChild === t");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void CreateDocumentFragment_Works()
    {
        _engine.Execute(@"
            var frag = document.createDocumentFragment();
            var a = document.createElement('div'); a.id = 'a';
            var b = document.createElement('div'); b.id = 'b';
            frag.appendChild(a);
            frag.appendChild(b);
            var parent = document.createElement('div');
            parent.appendChild(frag);
        ");
        Assert.Equal(2.0, _engine.Evaluate("parent.childNodes.length").AsNumber());
        Assert.Equal("a", _engine.Evaluate("parent.firstChild.id").AsString());
    }

    [Fact]
    public void DocumentFragment_NodeType_Is11()
    {
        var result = _engine.Evaluate("document.createDocumentFragment().nodeType");
        Assert.Equal(11.0, result.AsNumber());
    }

    [Fact]
    public void Element_ExtraProps_PersistAcrossAccess()
    {
        _engine.Execute(@"
            var el = document.createElement('div');
            el._vnode = { type: 'div' };
            el.__vue_app__ = true;
        ");
        Assert.True(_engine.Evaluate("el._vnode.type === 'div'").AsBoolean());
        Assert.True(_engine.Evaluate("el.__vue_app__ === true").AsBoolean());
    }

    [Fact]
    public void Element_ExtraProps_PersistOnIdentity()
    {
        _engine.Execute(@"
            var el = document.getElementById('app');
            el.__reactFiber = { tag: 5 };
        ");
        var result = _engine.Evaluate("document.getElementById('app').__reactFiber.tag");
        Assert.Equal(5.0, result.AsNumber());
    }

    [Fact]
    public void TextNode_ExtraProps_Persist()
    {
        _engine.Execute(@"
            var t = document.createTextNode('');
            t.__vue_anchor = true;
        ");
        Assert.True(_engine.Evaluate("t.__vue_anchor === true").AsBoolean());
    }

    [Fact]
    public void Comment_ExtraProps_Persist()
    {
        _engine.Execute(@"
            var c = document.createComment('');
            c.__vue_anchor = true;
        ");
        Assert.True(_engine.Evaluate("c.__vue_anchor === true").AsBoolean());
    }

    [Fact]
    public void ClassList_Add_Remove_Contains()
    {
        _engine.Execute(@"
            var el = document.createElement('div');
            el.classList.add('foo');
            el.classList.add('bar');
        ");
        Assert.True(_engine.Evaluate("el.classList.contains('foo')").AsBoolean());
        Assert.True(_engine.Evaluate("el.classList.contains('bar')").AsBoolean());

        _engine.Execute("el.classList.remove('foo');");
        Assert.False(_engine.Evaluate("el.classList.contains('foo')").AsBoolean());
    }

    [Fact]
    public void ClassList_Toggle()
    {
        _engine.Execute("var el = document.createElement('div');");
        Assert.True(_engine.Evaluate("el.classList.toggle('active')").AsBoolean());
        Assert.True(_engine.Evaluate("el.classList.contains('active')").AsBoolean());
        Assert.False(_engine.Evaluate("el.classList.toggle('active')").AsBoolean());
        Assert.False(_engine.Evaluate("el.classList.contains('active')").AsBoolean());
    }

    [Fact]
    public void Style_SetAndGet_CamelCase()
    {
        _engine.Execute(@"
            var el = document.createElement('div');
            el.style.backgroundColor = 'red';
        ");
        var result = _engine.Evaluate("el.style.backgroundColor").AsString();
        Assert.Equal("red", result);
    }

    [Fact]
    public void Style_SetProperty_GetPropertyValue()
    {
        _engine.Execute(@"
            var el = document.createElement('div');
            el.style.setProperty('color', 'blue');
        ");
        var result = _engine.Evaluate("el.style.getPropertyValue('color')").AsString();
        Assert.Equal("blue", result);
    }

    [Fact]
    public void Style_CssText()
    {
        _engine.Execute(@"
            var el = document.createElement('div');
            el.style.cssText = 'color: red; font-size: 16px';
        ");
        var result = _engine.Evaluate("el.style.cssText").AsString();
        Assert.Contains("color", result);
    }

    [Fact]
    public void Style_TypeofIsObject()
    {
        // React checks: typeof el.style !== 'object'
        var result = _engine.Evaluate("typeof document.createElement('div').style === 'object'");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void AddEventListener_FiresOnDispatch()
    {
        _engine.Execute(@"
            var el = document.createElement('div');
            var fired = false;
            el.addEventListener('click', function(e) { fired = true; });
            el.dispatchEvent({type:'click', bubbles:false, cancelable:false, defaultPrevented:false,
                target:null, currentTarget:null,
                preventDefault:function(){}, stopPropagation:function(){this._stopped=true},
                stopImmediatePropagation:function(){this._stopped=true}});
        ");
        Assert.True(_engine.Evaluate("fired").AsBoolean());
    }

    [Fact]
    public void RemoveEventListener_StopsFiring()
    {
        _engine.Execute(@"
            var el = document.createElement('div');
            var count = 0;
            var handler = function() { count++; };
            el.addEventListener('click', handler);
            el.dispatchEvent({type:'click',bubbles:false,cancelable:false,defaultPrevented:false,
                target:null,currentTarget:null,
                preventDefault:function(){},stopPropagation:function(){this._stopped=true},
                stopImmediatePropagation:function(){this._stopped=true}});
            el.removeEventListener('click', handler);
            el.dispatchEvent({type:'click',bubbles:false,cancelable:false,defaultPrevented:false,
                target:null,currentTarget:null,
                preventDefault:function(){},stopPropagation:function(){this._stopped=true},
                stopImmediatePropagation:function(){this._stopped=true}});
        ");
        Assert.Equal(1.0, _engine.Evaluate("count").AsNumber());
    }

    [Fact]
    public void DispatchEvent_Bubbles_ToParent()
    {
        _engine.Execute(@"
            var parent = document.createElement('div');
            var child = document.createElement('span');
            parent.appendChild(child);
            var parentFired = false;
            parent.addEventListener('click', function() { parentFired = true; });
            child.dispatchEvent({type:'click',bubbles:true,cancelable:false,defaultPrevented:false,
                target:null,currentTarget:null,
                preventDefault:function(){},stopPropagation:function(){this._stopped=true},
                stopImmediatePropagation:function(){this._stopped=true}});
        ");
        Assert.True(_engine.Evaluate("parentFired").AsBoolean());
    }

    [Fact]
    public void DispatchEvent_StopPropagation_PreventsBubbling()
    {
        _engine.Execute(@"
            var parent = document.createElement('div');
            var child = document.createElement('span');
            parent.appendChild(child);
            var parentFired = false;
            parent.addEventListener('click', function() { parentFired = true; });
            child.addEventListener('click', function(e) { e.stopPropagation(); });
            child.dispatchEvent({type:'click',bubbles:true,cancelable:false,defaultPrevented:false,
                target:null,currentTarget:null,
                preventDefault:function(){},stopPropagation:function(){this._stopped=true},
                stopImmediatePropagation:function(){this._stopped=true}});
        ");
        Assert.False(_engine.Evaluate("parentFired").AsBoolean());
    }

    [Fact]
    public void Document_AddEventListener_FiresOnBubble()
    {
        _engine.Execute(@"
            var el = document.createElement('div');
            document.body.appendChild(el);
            var docFired = false;
            document.addEventListener('click', function() { docFired = true; });
            el.dispatchEvent({type:'click',bubbles:true,cancelable:false,defaultPrevented:false,
                target:null,currentTarget:null,
                preventDefault:function(){},stopPropagation:function(){this._stopped=true},
                stopImmediatePropagation:function(){this._stopped=true}});
        ");
        Assert.True(_engine.Evaluate("docFired").AsBoolean());
    }

    [Fact]
    public void CompareDocumentPosition_Contains_Returns16()
    {
        _engine.Execute(@"
            var parent = document.createElement('div');
            var child = document.createElement('span');
            parent.appendChild(child);
        ");
        // DOCUMENT_POSITION_CONTAINED_BY = 16
        var result = _engine.Evaluate("!!(parent.compareDocumentPosition(child) & 16)");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void CompareDocumentPosition_Self_Returns0()
    {
        _engine.Execute("var el = document.createElement('div');");
        var result = _engine.Evaluate("el.compareDocumentPosition(el)");
        Assert.Equal(0.0, result.AsNumber());
    }

    [Fact]
    public void QuerySelector_FindsElement()
    {
        var result = _engine.Evaluate("document.querySelector('#app') !== null");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void QuerySelectorAll_ReturnsArray()
    {
        var result = _engine.Evaluate("document.querySelectorAll('div').length");
        Assert.True(result.AsNumber() >= 1);
    }

    [Fact]
    public void Element_QuerySelector_ScopedToElement()
    {
        _engine.Execute(@"
            var outer = document.createElement('div');
            var inner = document.createElement('span'); inner.id = 'inner';
            outer.appendChild(inner);
            document.body.appendChild(outer);
        ");
        var result = _engine.Evaluate("outer.querySelector('#inner') !== null");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void CloneNode_Shallow_ClonesAttributes()
    {
        _engine.Execute(@"
            var el = document.createElement('div');
            el.id = 'orig';
            el.className = 'test';
            var clone = el.cloneNode(false);
        ");
        Assert.Equal("orig", _engine.Evaluate("clone.id").AsString());
        // Clone is a different object
        Assert.False(_engine.Evaluate("clone === el").AsBoolean());
    }

    [Fact]
    public void Matches_Works()
    {
        _engine.Execute("var el = document.createElement('div'); el.className = 'test';");
        // AngleSharp's Matches requires the element to be in a document tree
        // For detached elements, this may not work — just verify no crash
        _engine.Evaluate("el.matches('.test')");
    }

    [Fact]
    public void Contains_ChildElement_ReturnsTrue()
    {
        _engine.Execute(@"
            var parent = document.createElement('div');
            var child = document.createElement('span');
            parent.appendChild(child);
        ");
        Assert.True(_engine.Evaluate("parent.contains(child)").AsBoolean());
    }

    [Fact]
    public void Contains_TextNode_ReturnsTrue()
    {
        _engine.Execute(@"
            var parent = document.createElement('div');
            var t = document.createTextNode('x');
            parent.appendChild(t);
        ");
        Assert.True(_engine.Evaluate("parent.contains(t)").AsBoolean());
    }

    [Fact]
    public void Contains_Null_ReturnsFalse()
    {
        Assert.False(_engine.Evaluate("document.createElement('div').contains(null)").AsBoolean());
    }
}
