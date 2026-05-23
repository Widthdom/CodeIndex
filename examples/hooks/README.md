# Sample post-extraction hook

Build the sample after building `cdidx` in Release configuration:

```bash
dotnet build ../../src/CodeIndex/CodeIndex.csproj -c Release
dotnet build SamplePostExtractionHook.csproj -c Release
mkdir -p ~/.config/cdidx/hooks
cp bin/Release/net8.0/SamplePostExtractionHook.dll ~/.config/cdidx/hooks/
```

The sample implements `IPostExtractionHook` and marks extracted C# class symbols with a `sample_hook_class` sub-kind before they are persisted.
