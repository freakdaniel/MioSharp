using System.Net.Http;
using System.Text;
using System.Text.Json;
using AngleSharp.Dom;
using Jint;
using Jint.Native;

namespace Mio.Script;

/// <summary>
/// Hosts a Jint ES2021 engine with DOM bindings, real HTTP fetch, and window.mio.invoke bridge.
/// </summary>
public sealed class ScriptEngine : IDisposable
{
    private readonly Engine _engine;
    private readonly List<(int id, TimeSpan at, JsValue fn)> _timers = [];
    private readonly List<(int id, TimeSpan period, TimeSpan nextAt, JsValue fn)> _intervals = [];
    private readonly List<(int id, JsValue callback)> _rafCallbacks = [];
    private readonly Dictionary<string, Func<JsonElement, object?>> _invokeHandlers = [];
    private static readonly HttpClient _httpClient = new();
    private readonly DateTime _startTime = DateTime.UtcNow;
    private TimeSpan _currentElapsed;
    private int _timerIdCounter = 0;
    private int _rafIdCounter = 0;
    private IDocument? _document;
    private DocumentShim? _documentShim;

    /// <summary>Fires whenever JS mutates the DOM (triggers re-layout).</summary>
    public event Action? DomChanged;

    public ScriptEngine()
    {
        _engine = new Engine(cfg => cfg.AllowClrWrite());
    }

    /// <summary>Registers a named C# handler callable via window.mio.invoke(name, args) from JS.</summary>
    public void RegisterInvoke(string name, Func<JsonElement, object?> handler) =>
        _invokeHandlers[name] = handler;

    /// <summary>Binds the DOM document and installs Web APIs into the JS global scope.</summary>
    public void SetDocument(IDocument document)
    {
        _document = document;
        InstallWebApis();
        InstallDomBindings();
    }

    /// <summary>Executes a JS script string.</summary>
    public void Execute(string script, string? sourceName = null)
    {
        try
        {
            _engine.Execute(script);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Script] Error{(sourceName != null ? $" in {sourceName}" : "")}: {ex.Message}");
        }
    }

    /// <summary>Fires rAF callbacks, pending setTimeout callbacks, and advances setInterval timers.</summary>
    public void Tick(TimeSpan elapsed)
    {
        _currentElapsed = elapsed;

        // requestAnimationFrame fires first (before paint — same as browser semantics)
        var rafBatch = _rafCallbacks.ToList();
        _rafCallbacks.Clear();
        foreach (var (_, cb) in rafBatch)
        {
            try { _engine.Invoke(cb, JsValue.FromObject(_engine, elapsed.TotalMilliseconds)); }
            catch (Exception ex) { Console.Error.WriteLine($"[rAF] {ex.Message}"); }
        }

        // setTimeout
        var due = _timers.Where(t => t.at <= elapsed).ToList();
        foreach (var t in due)
        {
            _timers.Remove(t);
            try { _engine.Invoke(t.fn); }
            catch (Exception ex) { Console.Error.WriteLine($"[Timer] {ex.Message}"); }
        }

        // setInterval
        for (int i = 0; i < _intervals.Count; i++)
        {
            var iv = _intervals[i];
            if (iv.nextAt <= elapsed)
            {
                _intervals[i] = (iv.id, iv.period, elapsed + iv.period, iv.fn);
                try { _engine.Invoke(iv.fn); }
                catch (Exception ex) { Console.Error.WriteLine($"[Interval] {ex.Message}"); }
            }
        }
    }

    /// <summary>Dispatches a click event to the given element and bubbles up the DOM tree.</summary>
    public void FireClick(IElement element)
    {
        if (_documentShim == null) return;
        var el = element;
        while (el != null)
        {
            _documentShim.Wrap(el).FireEvent("click");
            el = el.ParentElement;
        }
    }

    public void Dispose() => _engine.Dispose();

    private void InstallWebApis()
    {
        _engine.SetValue("console", new ConsoleShim());

        _engine.Execute("var window = globalThis; window.mio = {};");

        // setTimeout / clearTimeout with proper IDs for cancellation
        _engine.SetValue("setTimeout", new Func<JsValue, int, int>((callback, delay) =>
        {
            var id = ++_timerIdCounter;
            _timers.Add((id, _currentElapsed + TimeSpan.FromMilliseconds(delay), callback));
            return id;
        }));
        _engine.SetValue("clearTimeout", new Action<int>(id =>
            _timers.RemoveAll(t => t.id == id)));

        // setInterval / clearInterval
        _engine.SetValue("setInterval", new Func<JsValue, int, int>((callback, delay) =>
        {
            var id = ++_timerIdCounter;
            var period = TimeSpan.FromMilliseconds(delay);
            _intervals.Add((id, period, _currentElapsed + period, callback));
            return id;
        }));
        _engine.SetValue("clearInterval", new Action<int>(id =>
            _intervals.RemoveAll(iv => iv.id == id)));

        // requestAnimationFrame / cancelAnimationFrame
        _engine.SetValue("requestAnimationFrame", new Func<JsValue, int>(cb =>
        {
            var id = ++_rafIdCounter;
            _rafCallbacks.Add((id, cb));
            return id;
        }));
        _engine.SetValue("cancelAnimationFrame", new Action<int>(id =>
            _rafCallbacks.RemoveAll(r => r.id == id)));

        // performance.now() — milliseconds since engine start
        _engine.SetValue("__perfNow__", new Func<double>(
            () => (DateTime.UtcNow - _startTime).TotalMilliseconds));

        // queueMicrotask — immediate synchronous call (Jint is single-threaded)
        _engine.SetValue("queueMicrotask", new Action<JsValue>(cb =>
        {
            try { _engine.Invoke(cb); }
            catch (Exception ex) { Console.Error.WriteLine($"[microtask] {ex.Message}"); }
        }));

        // Real HTTP fetch via HttpClient (for external network requests ONLY)
        _engine.SetValue("__fetchImpl__", new Func<string, string, string?, string>((url, method, bodyJson) =>
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), url);
            if (bodyJson != null)
                request.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
            try
            {
                var response = _httpClient.SendAsync(request).GetAwaiter().GetResult();
                var responseBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                return JsonSerializer.Serialize(new
                {
                    status = (int)response.StatusCode,
                    ok = response.IsSuccessStatusCode,
                    body = responseBody
                });
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { __error__ = ex.Message });
            }
        }));

        // window.mio.invoke — in-process C# call bridge (ONLY way to talk to C# backend)
        _engine.SetValue("__mioInvoke__", new Func<string, string, string>((cmd, argsJson) =>
        {
            if (!_invokeHandlers.TryGetValue(cmd, out var handler))
                throw new InvalidOperationException($"mio.invoke: no handler registered for '{cmd}'");
            var args = JsonSerializer.Deserialize<JsonElement>(argsJson);
            return JsonSerializer.Serialize(handler(args));
        }));

        // JS layer: sync Promise helpers, fetch wrapper, mio.invoke wrapper, stubs
        _engine.Execute(@"
(function() {
    // Sync thenable helpers — used by both fetch and mio.invoke
    function __mioResolve__(value) {
        return {
            _v: value, _ok: true,
            then: function(f) {
                try {
                    var r = f(this._v);
                    return (r && typeof r.then === 'function') ? r : __mioResolve__(r);
                } catch(e) { return __mioReject__(e); }
            },
            catch: function() { return this; },
            finally: function(f) { try { f(); } catch(e) {} return this; }
        };
    }
    function __mioReject__(err) {
        return {
            _e: err, _ok: false,
            then: function() { return this; },
            catch: function(f) { try { f(this._e); } catch(e) {} return this; },
            finally: function(f) { try { f(); } catch(e) {} return this; }
        };
    }
    window.__mioResolve__ = __mioResolve__;
    window.__mioReject__  = __mioReject__;

    // Sync Promise override (Jint's native Promise is async and incompatible)
    var SyncPromise = {
        resolve: function(v) { return (v && typeof v.then === 'function') ? v : __mioResolve__(v); },
        reject:  function(e) { return __mioReject__(e); },
        all: function(arr) {
            var results = [];
            for (var i = 0; i < arr.length; i++) {
                var p = arr[i];
                if (p && typeof p.then === 'function') {
                    var val;
                    p.then(function(v) { val = v; });
                    results.push(val);
                } else { results.push(p); }
            }
            return __mioResolve__(results);
        },
        allSettled: function(arr) {
            return SyncPromise.all(arr);
        }
    };
    try { Promise = SyncPromise; } catch(e) {}
    try { globalThis.Promise = SyncPromise; } catch(e) {}

    // performance.now
    window.performance = { now: function() { return __perfNow__(); }, timeOrigin: 0 };

    // fetch — real HTTP via HttpClient. Use ONLY for external network requests.
    // To call C# backend, use window.mio.invoke() instead.
    window.fetch = function(url, options) {
        var method = (options && options.method) ? options.method : 'GET';
        var body   = (options && options.body)   ? options.body   : null;
        var raw    = __fetchImpl__(url, method, body);
        var parsed = JSON.parse(raw);
        if (parsed.__error__) return __mioReject__(new Error(parsed.__error__));
        var resp = {
            status: parsed.status, ok: parsed.ok,
            json: function() { return __mioResolve__(JSON.parse(parsed.body)); },
            text: function() { return __mioResolve__(parsed.body); },
            arrayBuffer: function() { return __mioReject__(new Error('arrayBuffer() not supported')); }
        };
        return __mioResolve__(resp);
    };

    // window.mio.invoke — THE bridge between JS and C# backend
    window.mio.invoke = function(cmd, args) {
        var json = JSON.stringify(args !== undefined ? args : null);
        try {
            return __mioResolve__(JSON.parse(__mioInvoke__(cmd, json)));
        } catch(e) {
            return __mioReject__(e);
        }
    };

    // Browser environment stubs
    window.location  = { href: '/', pathname: '/', search: '', hash: '', origin: 'mio://app' };
    window.history   = { pushState: function(){}, replaceState: function(){}, back: function(){} };
    window.navigator = { userAgent: 'MioSharp/1.0', platform: 'Win32', language: 'en-US', onLine: true };
    window.screen    = { width: 1280, height: 720, availWidth: 1280, availHeight: 720 };
    window.CustomEvent = function(type, opts) {
        return { type: type, detail: opts && opts.detail, bubbles: opts && opts.bubbles || false,
                 preventDefault: function(){}, stopPropagation: function(){} };
    };
    window.Event = window.CustomEvent;
    window.getComputedStyle = function(el) { return el && el.style ? el.style : {}; };
    window.matchMedia = function() { return { matches: false, addListener: function(){}, removeListener: function(){} }; };
    window.MutationObserver = function(cb) {
        return { observe: function(){}, disconnect: function(){}, takeRecords: function(){ return []; } };
    };
    window.ResizeObserver = window.MutationObserver;
    window.IntersectionObserver = window.MutationObserver;

    // DOM constructor stubs — Vue/React use instanceof checks and typeof guards against these.
    // Stubs must be DEFINED so that: e instanceof SVGElement does not throw ReferenceError.
    // instanceof always returns false for our C# shims, which is correct:
    // the app mount container (div#app) is never an SVGElement.
    // SVG elements inside the app are detected via the data-ns attribute set by createElementNS.
    window.Node             = function Node(){};
    window.Element          = function Element(){};
    window.HTMLElement      = function HTMLElement(){};
    window.SVGElement       = function SVGElement(){};
    window.Document         = function Document(){};
    window.ShadowRoot       = function ShadowRoot(){};
    window.Text             = function Text(){};
    window.Comment          = function Comment(){};
    window.DocumentFragment = function DocumentFragment(){};
    window.MathMLElement    = void 0; // typeof MathMLElement is not function (intentional)

    // Override Symbol.hasInstance so that 'el instanceof SVGElement/HTMLElement/Element' works
    // on our C# shims. Our ElementShim exposes getAttribute(), nodeType, etc. via reflection.
    // SVG elements are tagged with data-ns='http://www.w3.org/2000/svg' by createElementNS.
    try {
        Object.defineProperty(SVGElement, Symbol.hasInstance, {
            value: function(el) {
                return el != null && typeof el.getAttribute === 'function'
                    && el.getAttribute('data-ns') === 'http://www.w3.org/2000/svg';
            }
        });
        Object.defineProperty(Element, Symbol.hasInstance, {
            value: function(el) {
                return el != null && typeof el.nodeType === 'number' && el.nodeType === 1;
            }
        });
        Object.defineProperty(HTMLElement, Symbol.hasInstance, {
            value: function(el) {
                return el != null && typeof el.nodeType === 'number' && el.nodeType === 1
                    && (typeof el.getAttribute !== 'function' || el.getAttribute('data-ns') == null);
            }
        });
        Object.defineProperty(Node, Symbol.hasInstance, {
            value: function(el) {
                return el != null && typeof el.nodeType === 'number';
            }
        });
    } catch(e) {}
})();
");
    }

    private void InstallDomBindings()
    {
        if (_document == null) return;

        _documentShim = new DocumentShim(_document, _engine);
        _documentShim.DomMutated += () => DomChanged?.Invoke();
        _engine.SetValue("document", _documentShim);
        _engine.Execute("window.document = document;");
    }
}

internal sealed class ConsoleShim
{
    public void log(params object[] args) => Console.WriteLine("[JS] " + string.Join(" ", args));
    public void warn(params object[] args) => Console.WriteLine("[JS WARN] " + string.Join(" ", args));
    public void error(params object[] args) => Console.Error.WriteLine("[JS ERR] " + string.Join(" ", args));
    public void info(params object[] args) => Console.WriteLine("[JS INFO] " + string.Join(" ", args));
    public void debug(params object[] args) => Console.WriteLine("[JS DEBUG] " + string.Join(" ", args));
}
