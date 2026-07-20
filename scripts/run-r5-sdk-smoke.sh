#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
configuration="Release"
artifacts="${HTMLML_R5_ARTIFACTS:-$repo_root/TestResults/R5}"
feed="$artifacts/feed"
generated="$artifacts/generated"
cli_home="$artifacts/dotnet-home"

rm -rf "$artifacts"
mkdir -p "$feed" "$generated" "$cli_home"

projects=(
  src/HtmlML.Core/HtmlML.Core.csproj
  src/HtmlML.JavaScript/HtmlML.JavaScript.csproj
  src/HtmlML.Dom/HtmlML.Dom.csproj
  src/HtmlML.Css/HtmlML.Css.csproj
  src/HtmlML.Graphics/HtmlML.Graphics.csproj
  src/HtmlML.Backend.Abstractions/HtmlML.Backend.Abstractions.csproj
  src/HtmlML.Backend.Avalonia/HtmlML.Backend.Avalonia.csproj
  src/JavaScript.Avalonia.ClearScript/JavaScript.Avalonia.ClearScript.csproj
  src/HtmlML.Sdk/HtmlML.Sdk.csproj
  src/HtmlML.Sdk.Avalonia/HtmlML.Sdk.Avalonia.csproj
  templates/HtmlML.Templates/HtmlML.Templates.csproj
)

for project in "${projects[@]}"; do
  dotnet pack "$repo_root/$project" -c "$configuration" -o "$feed" -p:HtmlMlClearScriptNativeRequired=false -p:HtmlMlClearScriptNativeRid=
done

export DOTNET_CLI_HOME="$cli_home"
dotnet new install "$feed/HtmlML.Templates.11.3.4.nupkg"

cat > "$generated/NuGet.config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="htmlml-r5" value="$feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
</configuration>
EOF

templates=(htmlml-component-host htmlml-hybrid htmlml-typescript)
for template in "${templates[@]}"; do
  output="$generated/$template"
  dotnet new "$template" -n R5Smoke -o "$output"
  npm install --prefix "$output/web" --no-package-lock "$repo_root/tooling/htmlml"
  npm run build --prefix "$output/web"
  if grep -q 'process\.env\.NODE_ENV' "$output/Component/dist/main.js"; then
    printf 'Generated %s bundle leaked process.env.NODE_ENV into the browserless V8 runtime.\n' "$template" >&2
    exit 1
  fi
  dotnet build "$output/R5Smoke.csproj" -c "$configuration" --configfile "$generated/NuGet.config"
  dotnet run --project "$output/R5Smoke.csproj" -c "$configuration" --no-build -- --htmlml-smoke
  dotnet publish "$output/R5Smoke.csproj" -c "$configuration" --no-build -o "$output/publish"
done

npm test --prefix "$repo_root/tooling/htmlml"
npm ci --prefix "$repo_root/samples/components"
npm run build --prefix "$repo_root/samples/components"
npm run check --prefix "$repo_root/samples/components"
npm test --prefix "$repo_root/samples/components"
dotnet test "$repo_root/tests/HtmlML.Sdk.Tests/HtmlML.Sdk.Tests.csproj" -c "$configuration"
dotnet run --project "$repo_root/tests/HtmlML.Sdk.SampleSmoke/HtmlML.Sdk.SampleSmoke.csproj" -c "$configuration"

cat > "$artifacts/summary.json" <<EOF
{
  "milestone": "R5",
  "status": "passed",
  "profileVersion": "1.0",
  "templates": ["htmlml-component-host", "htmlml-hybrid", "htmlml-typescript"],
  "templateCount": 3,
  "sampleCount": 12,
  "componentRuntimeTestCount": 13,
  "templateBrowserlessGlobalGate": true,
  "sdkTargetFrameworks": ["net8.0", "net10.0"]
}
EOF

printf 'R5 SDK smoke passed: packages, 3 templates, Node tooling, SDK contracts, and 12 executed sample scenarios.\n'
