# HtmlML.Templates

Install the package, then choose one of the three R5 product shapes:

```sh
dotnet new install HtmlML.Templates
dotnet new htmlml-component-host -n MyComponentHost
dotnet new htmlml-hybrid -n MyHybridApp
dotnet new htmlml-typescript -n MyTypeScriptApp
```

Each template contains an offline prebuilt Component Profile 1 bundle so the .NET
project can be packaged deterministically, plus React/TypeScript/Vite source for the
normal development workflow. A reviewed native V8 package for the target RID is
required to execute the component.
