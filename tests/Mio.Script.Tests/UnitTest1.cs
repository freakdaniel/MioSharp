using Jint;

namespace Mio.Script.Tests;

public class JintInteropTests
{
    // Verify Jint 4.x finds lowercase C# properties (not get_/set_ methods)
    private class LowercasePropShim
    {
        private string? _val;
        public string? textContent
        {
            get => _val;
            set => _val = value;
        }
        public bool mutated;
    }

    [Fact]
    public void LowercaseProperty_Getter_Works()
    {
        var engine = new Engine(cfg => cfg.AllowClrWrite());
        var shim = new LowercasePropShim();
        shim.textContent = "world";
        engine.SetValue("obj", shim);
        var result = engine.Evaluate("obj.textContent").AsString();
        Assert.Equal("world", result);
    }

    [Fact]
    public void LowercaseProperty_Setter_Works()
    {
        var engine = new Engine(cfg => cfg.AllowClrWrite());
        var shim = new LowercasePropShim();
        engine.SetValue("obj", shim);
        engine.Execute("obj.textContent = 'hello';");
        Assert.Equal("hello", shim.textContent);
    }

    // End-to-end: setInterval fires, textContent setter is called, mutation flag set
    private class MutationShim(Action onMutate)
    {
        private string? _text;
        public string? textContent
        {
            get => _text;
            set { _text = value; onMutate(); }
        }
    }

    [Fact]
    public void DomMutation_ViaTextContentProperty_FiresEvent()
    {
        var engine = new Engine(cfg => cfg.AllowClrWrite());
        bool mutated = false;
        var shim = new MutationShim(() => mutated = true);
        engine.SetValue("obj", shim);
        engine.Execute("obj.textContent = 'test';");
        Assert.Equal("test", shim.textContent);
        Assert.True(mutated);
    }

    [Fact]
    public void SetInterval_FiresAfterElapsed()
    {
        var engine = new Engine(cfg => cfg.AllowClrWrite());
        var intervals = new List<(TimeSpan period, TimeSpan nextAt, Jint.Native.JsValue fn)>();
        TimeSpan current = TimeSpan.Zero;

        engine.SetValue("setInterval", new Action<Jint.Native.JsValue, int>((fn, delay) =>
        {
            var period = TimeSpan.FromMilliseconds(delay);
            intervals.Add((period, current + period, fn));
        }));

        engine.Execute("var fired = false; setInterval(function() { fired = true; }, 500);");

        current = TimeSpan.FromMilliseconds(600);
        foreach (var iv in intervals.ToList())
            if (iv.nextAt <= current)
                engine.Invoke(iv.fn);

        Assert.True(engine.Evaluate("fired").AsBoolean());
    }
}
