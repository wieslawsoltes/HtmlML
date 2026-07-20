using Microsoft.ClearScript.V8;

using var runtime = new V8Runtime();
using var owner = runtime.CreateScriptEngine("package-smoke-owner");
using var frame = runtime.CreateScriptEngine("package-smoke-frame");

owner.Execute("""
    globalThis.__packageSmokeBridge = Object.freeze({
      value: 41,
      add: function(value) { return this.value + value; }
    });
    """);
frame.Script.ownerWindow = owner.Script;

var result = frame.Evaluate("ownerWindow.__packageSmokeBridge.add(1)");
if (Convert.ToInt32(result) != 42)
{
    Console.Error.WriteLine($"V8 native package consumer: fail; bridge-result={result}");
    return 1;
}

// Exercise lazy compilation and tier-up through the shared-context boundary.
// A malformed native build can pass a single cross-context call but abort when
// V8 replaces the function's code after the call site warms up.
var warmedResult = frame.Evaluate("""
    (function() {
      let result = 0;
      for (let index = 0; index < 10000; index++) {
        result = ownerWindow.__packageSmokeBridge.add(1);
      }
      return result;
    })()
    """);
if (Convert.ToInt32(warmedResult) != 42)
{
    Console.Error.WriteLine($"V8 native package consumer: fail; warmed-bridge-result={warmedResult}");
    return 1;
}

Console.WriteLine(
    $"V8 native package consumer: pass; bridge-result={result}, " +
    $"warmed-bridge-result={warmedResult}");
return 0;
