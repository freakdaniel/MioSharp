using AngleSharp;
using AngleSharp.Dom;
using Mio.Script;

namespace Mio.Script.Tests;

/// <summary>
/// Tests for ScriptEngine: timers, promises, event dispatch, and browser API stubs.
/// </summary>
public class ScriptEngineTests : IDisposable
{
    private readonly ScriptEngine _scriptEngine;
    private readonly IDocument _doc;

    public ScriptEngineTests()
    {
        _scriptEngine = new ScriptEngine();
        var context = BrowsingContext.New(Configuration.Default);
        _doc = context.OpenAsync(req => req.Content("<html><head></head><body><div id='app'></div></body></html>"))
            .GetAwaiter().GetResult();
        _scriptEngine.SetDocument(_doc);
    }

    public void Dispose()
    {
        _scriptEngine.Dispose();
    }

    [Fact]
    public void SetTimeout_FiresAfterDelay()
    {
        _scriptEngine.Execute("var fired = false; setTimeout(function() { fired = true; }, 100);");
        // Not fired yet at t=0
        _scriptEngine.Tick(TimeSpan.FromMilliseconds(50));
        Assert.Equal("false", Eval("fired"));

        // Fired at t=150
        _scriptEngine.Tick(TimeSpan.FromMilliseconds(150));
        Assert.Equal("true", Eval("fired"));
    }

    [Fact]
    public void ClearTimeout_PreventsExecution()
    {
        _scriptEngine.Execute("var fired = false; var id = setTimeout(function() { fired = true; }, 100); clearTimeout(id);");
        _scriptEngine.Tick(TimeSpan.FromMilliseconds(200));
        Assert.Equal("false", Eval("fired"));
    }

    [Fact]
    public void SetInterval_FiresRepeatedly()
    {
        _scriptEngine.Execute("var count = 0; var id = setInterval(function() { count++; }, 100);");
        _scriptEngine.Tick(TimeSpan.FromMilliseconds(150));
        Assert.Equal("1", Eval("count"));
        _scriptEngine.Tick(TimeSpan.FromMilliseconds(250));
        Assert.Equal("2", Eval("count"));
    }

    [Fact]
    public void ClearInterval_Stops()
    {
        _scriptEngine.Execute("var count = 0; var id = setInterval(function() { count++; }, 100);");
        _scriptEngine.Tick(TimeSpan.FromMilliseconds(150));
        _scriptEngine.Execute("clearInterval(id);");
        _scriptEngine.Tick(TimeSpan.FromMilliseconds(350));
        Assert.Equal("1", Eval("count"));
    }

    [Fact]
    public void Promise_Resolve_FlushedInTick()
    {
        _scriptEngine.Execute("var result = ''; Promise.resolve('ok').then(function(v) { result = v; });");
        _scriptEngine.Tick(TimeSpan.Zero);
        Assert.Equal("ok", Eval("result"));
    }

    [Fact]
    public void MicrotasksFlushedAfterEachTimerCallback()
    {
        _scriptEngine.Execute(@"
            var log = [];
            setTimeout(function() {
                Promise.resolve().then(function() { log.push('microtask'); });
                log.push('timer');
            }, 10);
        ");
        _scriptEngine.Tick(TimeSpan.FromMilliseconds(20));
        // Both timer and its microtask should have fired
        Assert.Equal("timer", Eval("log[0]"));
        Assert.Equal("microtask", Eval("log[1]"));
    }

    [Fact]
    public void MicrotasksFlushedAfterInterval()
    {
        _scriptEngine.Execute(@"
            var result = 0;
            setInterval(function() {
                Promise.resolve().then(function() { result++; });
            }, 100);
        ");
        _scriptEngine.Tick(TimeSpan.FromMilliseconds(150));
        Assert.Equal("1", Eval("result"));
    }

    [Fact]
    public void QueueMicrotask_ExecutesSynchronously()
    {
        _scriptEngine.Execute("var fired = false; queueMicrotask(function() { fired = true; });");
        Assert.Equal("true", Eval("fired"));
    }

    [Fact]
    public void Window_Location_Exists()
    {
        Assert.Equal("/", Eval("window.location.pathname"));
    }

    [Fact]
    public void Window_Navigator_Exists()
    {
        Assert.Contains("MioSharp", Eval("window.navigator.userAgent"));
    }

    [Fact]
    public void Performance_Now_ReturnsNumber()
    {
        var result = Eval("typeof performance.now()");
        Assert.Equal("number", result);
    }

    [Fact]
    public void MatchMedia_ReturnsObject()
    {
        Assert.Equal("false", Eval("window.matchMedia('(min-width: 768px)').matches.toString()"));
    }

    [Fact]
    public void GetComputedStyle_ReturnsObject()
    {
        Assert.Equal("object", Eval("typeof window.getComputedStyle(document.body)"));
    }

    [Fact]
    public void GetSelection_ReturnsObject()
    {
        Assert.Equal("object", Eval("typeof window.getSelection()"));
    }

    [Fact]
    public void Event_Constructor_Works()
    {
        _scriptEngine.Execute("var e = new Event('test', {bubbles: true});");
        Assert.Equal("test", Eval("e.type"));
        Assert.Equal("true", Eval("e.bubbles.toString()"));
    }

    [Fact]
    public void CustomEvent_Constructor_Works()
    {
        _scriptEngine.Execute("var e = new CustomEvent('test', {detail: {x: 42}});");
        Assert.Equal("test", Eval("e.type"));
        Assert.Equal("42", Eval("e.detail.x.toString()"));
    }

    [Fact]
    public void MouseEvent_Constructor_Works()
    {
        _scriptEngine.Execute("var e = new MouseEvent('click', {bubbles: true});");
        Assert.Equal("click", Eval("e.type"));
        Assert.Equal("true", Eval("e.bubbles.toString()"));
    }

    [Fact]
    public void Element_InstanceOf_Node()
    {
        Assert.Equal("true", Eval("document.createElement('div') instanceof Node"));
    }

    [Fact]
    public void Element_InstanceOf_Element()
    {
        Assert.Equal("true", Eval("document.createElement('div') instanceof Element"));
    }

    [Fact]
    public void TextNode_InstanceOf_Text()
    {
        Assert.Equal("true", Eval("document.createTextNode('') instanceof Text"));
    }

    [Fact]
    public void Comment_InstanceOf_Comment()
    {
        Assert.Equal("true", Eval("document.createComment('') instanceof Comment"));
    }

    [Fact]
    public void DocumentFragment_InstanceOf_DocumentFragment()
    {
        Assert.Equal("true", Eval("document.createDocumentFragment() instanceof DocumentFragment"));
    }

    [Fact]
    public void MutationObserver_CanBeConstructed()
    {
        _scriptEngine.Execute("var mo = new MutationObserver(function(){}); mo.observe(document.body, {});");
        // No crash = success
    }

    [Fact]
    public void ResizeObserver_CanBeConstructed()
    {
        _scriptEngine.Execute("var ro = new ResizeObserver(function(){}); ro.observe(document.body);");
    }

    [Fact]
    public void MioInvoke_CallsRegisteredHandler()
    {
        _scriptEngine.RegisterInvoke("test", args => new { result = "ok" });
        _scriptEngine.Execute("var r = ''; window.mio.invoke('test', {}).then(function(v) { r = v.result; });");
        _scriptEngine.Tick(TimeSpan.Zero);
        Assert.Equal("ok", Eval("r"));
    }

    [Fact]
    public void FireClick_DispatchesToElement()
    {
        _scriptEngine.Execute(@"
            var clicked = false;
            var app = document.getElementById('app');
            app.addEventListener('click', function(e) { clicked = true; });
        ");
        var appEl = _doc.GetElementById("app")!;
        _scriptEngine.FireClick(appEl);
        Assert.Equal("true", Eval("clicked"));
    }

    [Fact]
    public void FireClick_HasMouseEventProperties()
    {
        _scriptEngine.Execute(@"
            var btn = -1;
            var app = document.getElementById('app');
            app.addEventListener('click', function(e) { btn = e.button; });
        ");
        _scriptEngine.FireClick(_doc.GetElementById("app")!);
        Assert.Equal("0", Eval("btn.toString()"));
    }

    [Fact]
    public void FireClick_BubblesToParent()
    {
        _scriptEngine.Execute(@"
            var child = document.createElement('div'); child.id = 'child';
            document.getElementById('app').appendChild(child);
            var parentClicked = false;
            document.getElementById('app').addEventListener('click', function() { parentClicked = true; });
        ");
        var child = _doc.GetElementById("child")!;
        _scriptEngine.FireClick(child);
        Assert.Equal("true", Eval("parentClicked"));
    }

    [Fact]
    public void DomChanged_FiresOnMutation()
    {
        var changed = false;
        _scriptEngine.DomChanged += () => changed = true;
        _scriptEngine.Execute("document.createElement('div');");
        Assert.True(changed);
    }

    private string Eval(string js)
    {
        try
        {
            var result = _scriptEngine.GetType()
                .GetField("_engine", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(_scriptEngine) as Jint.Engine;
            return result!.Evaluate(js).ToString();
        }
        catch (Exception ex)
        {
            return $"ERROR: {ex.Message}";
        }
    }
}
