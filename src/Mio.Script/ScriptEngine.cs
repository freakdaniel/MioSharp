using System.Net.Http;
using System.Text;
using System.Text.Json;
using AngleSharp.Dom;
using Jint;
using Jint.Native;

namespace Mio.Script;

/// <summary>
/// Hosts a Jint ES2021+ engine with DOM bindings, real HTTP fetch, and window.mio.invoke bridge.
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

    /// <summary>Window-level event listeners (resize, error, etc.).</summary>
    private readonly Dictionary<string, List<JsValue>> _windowListeners = [];

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
            catch (Exception ex) { Console.Error.WriteLine($"[rAF] {ex.Message}\n{ex.StackTrace}"); }
            FlushMicrotasks();
        }

        // setTimeout
        var due = _timers.Where(t => t.at <= elapsed).ToList();
        foreach (var t in due)
        {
            _timers.Remove(t);
            try { _engine.Invoke(t.fn); }
            catch (Exception ex) { Console.Error.WriteLine($"[Timer] {ex.Message}\n{ex.StackTrace}"); }
            FlushMicrotasks();
        }

        // setInterval
        for (int i = 0; i < _intervals.Count; i++)
        {
            var iv = _intervals[i];
            if (iv.nextAt <= elapsed)
            {
                _intervals[i] = (iv.id, iv.period, elapsed + iv.period, iv.fn);
                try { _engine.Invoke(iv.fn); }
                catch (Exception ex) { Console.Error.WriteLine($"[Interval] {ex.Message}\n{ex.StackTrace}"); }
                FlushMicrotasks();
            }
        }

        // Final flush for any remaining microtasks
        FlushMicrotasks();
    }

    /// <summary>Dispatches a click event to the given element using proper DOM event bubbling.</summary>
    public void FireClick(IElement element)
    {
        if (_documentShim == null) return;
        var shim = _documentShim.Wrap(element);
        // Create a proper MouseEvent with all properties Vue/React check
        var eventObj = _engine.Evaluate(
            "(function(){var e=new MouseEvent('click',{bubbles:true,cancelable:true});" +
            "e.button=0;e.detail=1;e.clientX=0;e.clientY=0;e.screenX=0;e.screenY=0;" +
            "e.pageX=0;e.pageY=0;e.offsetX=0;e.offsetY=0;" +
            "e.altKey=false;e.ctrlKey=false;e.metaKey=false;e.shiftKey=false;" +
            "e.which=1;e.isTrusted=true;return e})()");
        _engine.Invoke(shim.Get("dispatchEvent", shim), JsValue.Undefined, [eventObj]);
    }

    private void FlushMicrotasks()
    {
        try { _engine.Advanced.ProcessTasks(); }
        catch (Exception ex) { Console.Error.WriteLine($"[ProcessTasks] {ex.Message}\n{ex.StackTrace}"); }
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

        // window.addEventListener / removeEventListener
        _engine.SetValue("__windowAddEvt__", new Action<string, JsValue>((type, handler) =>
        {
            if (!_windowListeners.TryGetValue(type, out var list))
                _windowListeners[type] = list = [];
            list.Add(handler);
        }));
        _engine.SetValue("__windowRemoveEvt__", new Action<string, JsValue>((type, handler) =>
        {
            if (_windowListeners.TryGetValue(type, out var list))
                list.Remove(handler);
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

        // JS layer: Promise helpers, fetch wrapper, mio.invoke wrapper, browser stubs
        _engine.Execute(@"
(function() {
    // Sync thenable helpers — used by fetch and mio.invoke (sync C# calls)
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
            then: function(_, f) { if (f) { try { return __mioResolve__(f(this._e)); } catch(e) { return __mioReject__(e); } } return this; },
            catch: function(f) { try { return __mioResolve__(f(this._e)); } catch(e) { return __mioReject__(e); } },
            finally: function(f) { try { f(); } catch(e) {} return this; }
        };
    }
    window.__mioResolve__ = __mioResolve__;
    window.__mioReject__  = __mioReject__;

    // Keep native Promise intact — Jint supports it natively.
    // SyncPromise is only used for fetch/mio.invoke which are synchronous C# calls.

    // performance.now
    window.performance = { now: function() { return __perfNow__(); }, timeOrigin: 0 };

    // window.addEventListener / removeEventListener / dispatchEvent
    window.addEventListener = function(type, handler, options) { __windowAddEvt__(type, handler); };
    window.removeEventListener = function(type, handler, options) { __windowRemoveEvt__(type, handler); };
    window.dispatchEvent = function(event) { return true; };

    // fetch — real HTTP via HttpClient
    window.fetch = function(url, options) {
        var method = (options && options.method) ? options.method : 'GET';
        var body   = (options && options.body)   ? options.body   : null;
        var raw    = __fetchImpl__(url, method, body);
        var parsed = JSON.parse(raw);
        if (parsed.__error__) return Promise.reject(new Error(parsed.__error__));
        var resp = {
            status: parsed.status, ok: parsed.ok, headers: { get: function() { return null; } },
            json: function() { return Promise.resolve(JSON.parse(parsed.body)); },
            text: function() { return Promise.resolve(parsed.body); },
            arrayBuffer: function() { return Promise.reject(new Error('arrayBuffer() not supported')); }
        };
        return Promise.resolve(resp);
    };

    // window.mio.invoke — THE bridge between JS and C# backend
    window.mio.invoke = function(cmd, args) {
        var json = JSON.stringify(args !== undefined ? args : null);
        try {
            return Promise.resolve(JSON.parse(__mioInvoke__(cmd, json)));
        } catch(e) {
            return Promise.reject(e);
        }
    };

    // Browser environment stubs
    window.location  = { href: '/', pathname: '/', search: '', hash: '', origin: 'mio://app',
                         host: 'app', hostname: 'app', port: '', protocol: 'mio:',
                         assign: function(){}, replace: function(){}, reload: function(){} };
    window.history   = { pushState: function(){}, replaceState: function(){}, back: function(){},
                         forward: function(){}, go: function(){}, length: 1, state: null };
    window.navigator = { userAgent: 'MioSharp/1.0', platform: 'Win32', language: 'en-US',
                         languages: ['en-US'], onLine: true, cookieEnabled: false,
                         vendor: 'MioSharp', appVersion: '1.0' };
    window.screen    = { width: 1280, height: 720, availWidth: 1280, availHeight: 720,
                         colorDepth: 24, pixelDepth: 24, orientation: { type: 'landscape-primary', angle: 0 } };

    // Event constructors
    window.Event = function Event(type, opts) {
        opts = opts || {};
        this.type = type;
        this.bubbles = !!opts.bubbles;
        this.cancelable = !!opts.cancelable;
        this.composed = !!opts.composed;
        this.defaultPrevented = false;
        this.target = null;
        this.currentTarget = null;
        this.eventPhase = 0;
        this.timeStamp = __perfNow__();
        this.isTrusted = false;
    };
    window.Event.prototype.preventDefault = function() { this.defaultPrevented = true; };
    window.Event.prototype.stopPropagation = function() { this._stopped = true; };
    window.Event.prototype.stopImmediatePropagation = function() { this._stopped = true; };
    window.Event.prototype.composedPath = function() { return []; };

    window.CustomEvent = function CustomEvent(type, opts) {
        window.Event.call(this, type, opts);
        this.detail = (opts && opts.detail !== undefined) ? opts.detail : null;
    };
    window.CustomEvent.prototype = Object.create(window.Event.prototype);
    window.CustomEvent.prototype.constructor = window.CustomEvent;

    // KeyboardEvent, MouseEvent, FocusEvent stubs
    window.KeyboardEvent = function KeyboardEvent(type, opts) { window.Event.call(this, type, opts); this.key = (opts && opts.key) || ''; this.code = (opts && opts.code) || ''; };
    window.KeyboardEvent.prototype = Object.create(window.Event.prototype);
    window.MouseEvent = function MouseEvent(type, opts) { window.Event.call(this, type, opts); this.clientX = 0; this.clientY = 0; this.button = 0; };
    window.MouseEvent.prototype = Object.create(window.Event.prototype);
    window.FocusEvent = function FocusEvent(type, opts) { window.Event.call(this, type, opts); this.relatedTarget = null; };
    window.FocusEvent.prototype = Object.create(window.Event.prototype);
    window.InputEvent = function InputEvent(type, opts) { window.Event.call(this, type, opts); this.data = (opts && opts.data) || null; this.inputType = (opts && opts.inputType) || ''; };
    window.InputEvent.prototype = Object.create(window.Event.prototype);

    window.getComputedStyle = function(el) { return el && el.style ? el.style : {}; };
    window.getSelection = function() {
        return { anchorNode:null, anchorOffset:0, focusNode:null, focusOffset:0,
                 isCollapsed:true, rangeCount:0, type:'None',
                 addRange:function(){}, removeAllRanges:function(){}, collapse:function(){},
                 getRangeAt:function(){ return {startContainer:null,startOffset:0,endContainer:null,endOffset:0,
                     collapsed:true,commonAncestorContainer:null,
                     setStart:function(){},setEnd:function(){},cloneRange:function(){return this},
                     toString:function(){return ''}}; },
                 toString:function(){ return ''; } };
    };
    window.matchMedia = function(query) {
        return { matches: false, media: query || '',
                 addListener: function(){}, removeListener: function(){},
                 addEventListener: function(){}, removeEventListener: function(){},
                 dispatchEvent: function(){ return true; } };
    };
    window.MutationObserver = function(cb) {
        this._cb = cb;
        this.observe = function(){};
        this.disconnect = function(){};
        this.takeRecords = function(){ return []; };
    };
    window.ResizeObserver = function(cb) {
        this._cb = cb;
        this.observe = function(){};
        this.unobserve = function(){};
        this.disconnect = function(){};
    };
    window.IntersectionObserver = function(cb) {
        this._cb = cb;
        this.observe = function(){};
        this.unobserve = function(){};
        this.disconnect = function(){};
    };

    // TextEncoder / TextDecoder
    window.TextEncoder = function TextEncoder() { this.encoding = 'utf-8'; };
    window.TextEncoder.prototype.encode = function(str) {
        str = str || '';
        var arr = [];
        for (var i = 0; i < str.length; i++) {
            var c = str.charCodeAt(i);
            if (c < 128) arr.push(c);
            else if (c < 2048) { arr.push((c >> 6) | 192); arr.push((c & 63) | 128); }
            else { arr.push((c >> 12) | 224); arr.push(((c >> 6) & 63) | 128); arr.push((c & 63) | 128); }
        }
        return arr;
    };
    window.TextDecoder = function TextDecoder(encoding) { this.encoding = encoding || 'utf-8'; };
    window.TextDecoder.prototype.decode = function(arr) {
        if (!arr || !arr.length) return '';
        var s = '';
        for (var i = 0; i < arr.length; i++) s += String.fromCharCode(arr[i] & 0xFF);
        return s;
    };

    // AbortController stub
    window.AbortController = function AbortController() {
        this.signal = { aborted: false, addEventListener: function(){}, removeEventListener: function(){} };
        this.abort = function() { this.signal.aborted = true; };
    };

    // DOM constructor stubs — Vue/React use instanceof checks and typeof guards.
    window.Node             = function Node(){};
    window.Element          = function Element(){};
    window.HTMLElement      = function HTMLElement(){};
    window.SVGElement       = function SVGElement(){};
    window.Document         = function Document(){};
    window.ShadowRoot       = function ShadowRoot(){};
    window.Text             = function Text(){};
    window.Comment          = function Comment(){};
    window.DocumentFragment = function DocumentFragment(){};
    window.MathMLElement    = void 0;

    // Override Symbol.hasInstance for proper instanceof checks on our C# shims
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
        Object.defineProperty(Document, Symbol.hasInstance, {
            value: function(el) {
                return el != null && typeof el.nodeType === 'number' && el.nodeType === 9;
            }
        });
        Object.defineProperty(DocumentFragment, Symbol.hasInstance, {
            value: function(el) {
                return el != null && typeof el.nodeType === 'number' && el.nodeType === 11;
            }
        });
        Object.defineProperty(Text, Symbol.hasInstance, {
            value: function(el) {
                return el != null && typeof el.nodeType === 'number' && el.nodeType === 3;
            }
        });
        Object.defineProperty(Comment, Symbol.hasInstance, {
            value: function(el) {
                return el != null && typeof el.nodeType === 'number' && el.nodeType === 8;
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

        // DocumentShim IS ObjectInstance IS JsValue — assign directly, no ObjectWrapper
        _engine.SetValue("document", (JsValue)_documentShim);
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
