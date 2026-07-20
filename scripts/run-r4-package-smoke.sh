#!/usr/bin/env bash

set -euo pipefail
export LC_ALL=C
export LANG=C

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

package_root="$repo_root/TestResults/R4/packages"
mkdir -p "$package_root"

version="$(sed -n -E 's:.*<VersionPrefix>([^<]+)</VersionPrefix>.*:\1:p' Directory.Build.props)"
if [[ -z "$version" ]]; then
  printf 'Unable to read VersionPrefix from Directory.Build.props.\n' >&2
  exit 1
fi

dotnet restore HtmlML.sln
dotnet run --project tests/HtmlML.Backend.PackageSmoke/HtmlML.Backend.PackageSmoke.csproj \
  -c Release

projects=(
  src/HtmlML.Core/HtmlML.Core.csproj
  src/HtmlML.Dom/HtmlML.Dom.csproj
  src/HtmlML.Css/HtmlML.Css.csproj
  src/HtmlML.Graphics/HtmlML.Graphics.csproj
  src/HtmlML.JavaScript/HtmlML.JavaScript.csproj
  src/HtmlML.Backend.Abstractions/HtmlML.Backend.Abstractions.csproj
  src/HtmlML.Backend.Avalonia/HtmlML.Backend.Avalonia.csproj
)
for project in "${projects[@]}"; do
  dotnet pack "$project" -c Release -o "$package_root" --no-restore
done

python3 scripts/verify-r4-packages.py "$package_root" \
  --output "$repo_root/TestResults/R4/package-graph.json"

config=tests/HtmlML.Backend.PackageSmoke/NuGet.local.config
smoke=tests/HtmlML.Backend.PackageSmoke/HtmlML.Backend.PackageSmoke.csproj
package_id=HtmlML.Backend.Avalonia
dotnet restore "$smoke" \
  -p:HtmlMlBackendPackageId="$package_id" \
  -p:HtmlMlBackendPackageVersion="$version" \
  --configfile "$config"
dotnet run --project "$smoke" -c Release --no-restore \
  -p:HtmlMlBackendPackageId="$package_id" \
  -p:HtmlMlBackendPackageVersion="$version"

# Leave the solution in its normal project-reference restore mode.
dotnet restore "$smoke"
printf 'R4 package graph and clean-consumer smokes passed.\n'
