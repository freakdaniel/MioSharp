using AngleSharp.Dom;
using Jint;
using Jint.Native;
using Mio.Routing;

namespace Mio.Script;

/// <summary>
/// Hosts a Jint ES2021 engine with DOM bindings and in-process fetch routing.
/// </summary>
public sealed class ScriptEngine : IDisposable
{
    private readonly Engine _engine;
    private readonly Router _router;
    private readonly List<(TimeSpan at, JsValue fn)> _timers = [];
    private readonly List<(TimeSpan period, TimeSpan nextAt, JsValue fn)> _intervals = [];
    private TimeSpan _currentElapsed;
    private IDocument? _document;
    private DocumentShim? _documentShim;

    /// <summary>Fires whenever JS mutates the DOM (triggers re-layout).</summary>
    public event Action? DomChanged;

    public ScriptEngine(Router router)
    {
        _router = router;
        _engine = new Engine(cfg => cfg.AllowClrWrite());
    }

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

    /// <summary>
    /// Registers a C# delegate as window.mio.{name}() callable from JS.
    /// </summary>
    public void Register(string name, Delegate fn)
    {
        // Temporarily set as global, then attach to window.mio
        _engine.SetValue("__mio_reg_temp__", fn);
        _engine.Execute($"window.mio['{name}'] = __mio_reg_temp__;");
        _engine.SetValue("__mio_reg_temp__", JsValue.Undefined);
    }

    /// <summary>Fires pending setTimeout callbacks whose time has elapsed.</summary>
    public void Tick(TimeSpan elapsed)
    {
        _currentElapsed = elapsed;

        var due = _timers.Where(t => t.at <= elapsed).ToList();
        foreach (var t in due)
        {
            _timers.Remove(t);
            try { _engine.Invoke(t.fn); }
            catch (Exception ex) { Console.Error.WriteLine($"[Timer] {ex.Message}"); }
        }

        for (int i = 0; i < _intervals.Count; i++)
        {
            var iv = _intervals[i];
            if (iv.nextAt <= elapsed)
            {
                _intervals[i] = (iv.period, elapsed + iv.period, iv.fn);
                try { _engine.Invoke(iv.fn); }
                catch (Exception ex) { Console.Error.WriteLine($"[Interval] {ex.Message}"); }
            }
        }
    }

    public void Dispose() => _engine.Dispose();

    private void InstallWebApis()
    {
        _engine.SetValue("console", new ConsoleShim());

        _engine.Execute("var window = globalThis; window.mio = {};");

        _engine.SetValue("setTimeout", new Action<JsValue, int>((callback, delay) =>
        {
            _timers.Add((_currentElapsed + TimeSpan.FromMilliseconds(delay), callback));
        }));
        _engine.SetValue("clearTimeout", new Action<int>(_ => { }));
        _engine.SetValue("setInterval", new Action<JsValue, int>((callback, delay) =>
        {
            var period = TimeSpan.FromMilliseconds(delay);
            _intervals.Add((period, _currentElapsed + period, callback));
        }));
        _engine.SetValue("clearInterval", new Action<int>(_ => { }));

        // Synchronous Promise implementation, overrides Jint's native async Promise
        // so that fetch().then().then() chains execute immediately during script execution.
        // Promise.resolve unwraps thenables (per spec) so chained .then(r => r.json()) works... probably...
        _engine.Execute(@"
(function() {
    var SyncPromise = {
        resolve: function(v) {
            // Unwrap thenables — critical for .then(r => r.json()).then(data => data.message)
            if (v !== null && v !== undefined && typeof v === 'object' && typeof v.then === 'function') return v;
            return {
                _v: v, _ok: true,
                then: function(f) {
                    if (!this._ok) return this;
                    try { return SyncPromise.resolve(f(this._v)); } catch(e) { return SyncPromise.reject(e); }
                },
                catch: function(f) { return this; },
                finally: function(f) { try { f(); } catch(e) {} return this; }
            };
        },
        reject: function(e) {
            return {
                _e: e, _ok: false,
                then: function(f) { return this; },
                catch: function(f) {
                    try { return SyncPromise.resolve(f(this._e)); } catch(e2) { return SyncPromise.reject(e2); }
                },
                finally: function(f) { try { f(); } catch(e) {} return this; }
            };
        }
    };
    // Replace global Promise — works in non-strict mode even if native Promise exists
    try { Promise = SyncPromise; } catch(e) {}
    try { globalThis.Promise = SyncPromise; } catch(e) {}
})();
");

        // fetch — in-process dispatch, returns a Promise-like object
        _engine.SetValue("__fetchImpl__", new Func<string, JsValue>((url) =>
        {
            try
            {
                var response = _router.Dispatch("GET", url);
                var body = System.Text.Json.JsonSerializer.Serialize(response.BodyText);
                var status = response.Status;
                var ok = status < 400 ? "true" : "false";
                var contentType = response.ContentType.Replace("'", "\\'");
                var script = $@"({{
ok: {ok},
status: {status},
headers: {{'content-type': '{contentType}'}},
json: function() {{
  var b = {body};
  try {{
    var parsed = JSON.parse(b);
    return {{ _v: parsed, _ok: true,
      then: function(f) {{ if(!this._ok) return this; try {{ var r=f(this._v); return (r&&typeof r.then==='function')?r:{{_v:r,_ok:true,then:function(){{return this;}},catch:function(){{return this;}}}}; }} catch(e) {{ return {{_e:e,_ok:false,then:function(){{return this;}},catch:function(fc){{fc(this._e);return this;}}}}; }} }},
      catch: function() {{ return this; }}
    }};
  }} catch(e) {{
    return {{ _e:e, _ok:false, then:function(){{return this;}}, catch:function(f){{try{{f(this._e);}}catch(e2){{}} return this;}} }};
  }}
}},
text: function() {{
  var t = {body};
  return {{ _v: t, _ok: true,
    then: function(f) {{ if(!this._ok) return this; try {{ var r=f(this._v); return (r&&typeof r.then==='function')?r:{{_v:r,_ok:true,then:function(){{return this;}},catch:function(){{return this;}}}}; }} catch(e) {{ return {{_e:e,_ok:false,then:function(){{return this;}},catch:function(fc){{fc(this._e);return this;}}}}; }} }},
    catch: function() {{ return this; }}
  }};
}}
}})";
                return _engine.Evaluate(script);
            }
            catch (Exception ex)
            {
                var msg = System.Text.Json.JsonSerializer.Serialize(ex.Message);
                return _engine.Evaluate($"({{ ok: false, status: 500, json: function(){{ return {{_e:new Error({msg}),_ok:false,then:function(){{return this;}},catch:function(f){{f(this._e);return this;}}}}; }}, text: function(){{ return {{_e:new Error({msg}),_ok:false,then:function(){{return this;}},catch:function(f){{f(this._e);return this;}}}}; }} }})");

            }
        }));
        _engine.Execute(@"
function fetch(url, opts) {
    var r = __fetchImpl__(url);
    // Return a synchronous thenable that doesn't rely on the global Promise binding
    return {
        _v: r, _ok: true,
        then: function(f) {
            if (!this._ok) return this;
            try {
                var result = f(this._v);
                // Unwrap thenable (e.g. r.json() returns a promise-like)
                if (result !== null && result !== undefined && typeof result === 'object' && typeof result.then === 'function') return result;
                return { _v: result, _ok: true,
                    then: function(f2) {
                        if (!this._ok) return this;
                        try { var r2 = f2(this._v); return (r2 && typeof r2.then === 'function') ? r2 : { _v: r2, _ok: true, then: function() { return this; }, catch: function() { return this; } }; } catch(e2) { return { _e: e2, _ok: false, then: function() { return this; }, catch: function(fc) { fc(this._e); return this; } }; }
                    },
                    catch: function() { return this; }
                };
            } catch(e) {
                return { _e: e, _ok: false, then: function() { return this; }, catch: function(f) { try { f(this._e); } catch(e2) {} return this; } };
            }
        },
        catch: function(f) { return this; }
    };
}
");

        // location stub
        _engine.Execute("window.location = { href: '/', pathname: '/', search: '', hash: '' };");
        _engine.Execute("window.history = { pushState: function(){}, replaceState: function(){} };");
        _engine.Execute("window.navigator = { userAgent: 'MioSharp/1.0' };");
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
