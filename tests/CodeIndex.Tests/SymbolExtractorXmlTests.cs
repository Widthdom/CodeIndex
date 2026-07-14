using System.Diagnostics;
using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Xml;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Models;

namespace CodeIndex.Tests;

public partial class SymbolExtractorTests
{
    [Fact]
    public void Extract_NuGetConfig_EmitsSecurityPolicyValues_Issue4459()
    {
        const string content = """
            <configuration>
              <config>
                <add key="signatureValidationMode" value="require" />
              </config>
              <packageSources>
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="nuget.org">
                  <package pattern="CodeIndex.*" />
                </packageSource>
              </packageSourceMapping>
              <trustedSigners>
                <author name="Example Author">
                  <certificate fingerprint="AABBCCDD" hashAlgorithm="SHA256" allowUntrustedRoot="false" />
                </author>
                <repository name="nuget.org" serviceIndex="https://api.nuget.org/v3/index.json">
                  <certificate fingerprint="11223344" hashAlgorithm="SHA256" allowUntrustedRoot="true" />
                </repository>
              </trustedSigners>
            </configuration>
            """;

        var symbols = SymbolExtractor.Extract(1, "xml", content);

        Assert.Contains(symbols, symbol => symbol.Name == "nuget.org" && symbol.SubKind == "nuget.package_source");
        Assert.Contains(symbols, symbol => symbol.Name == "https://api.nuget.org/v3/index.json" && symbol.SubKind == "nuget.package_source_url");
        Assert.Contains(symbols, symbol => symbol.Name == "nuget.org" && symbol.SubKind == "nuget.package_source_mapping");
        Assert.Contains(symbols, symbol => symbol.Name == "CodeIndex.*" && symbol.SubKind == "nuget.package_source_mapping_pattern");
        Assert.Contains(symbols, symbol => symbol.Name == "require" && symbol.SubKind == "nuget.signature_validation_mode");
        Assert.Contains(symbols, symbol => symbol.Name == "Example Author" && symbol.SubKind == "nuget.trusted_signer");
        Assert.Contains(symbols, symbol => symbol.Name == "AABBCCDD" && symbol.SubKind == "nuget.certificate_fingerprint");
        Assert.Contains(symbols, symbol => symbol.Name == "false" && symbol.SubKind == "nuget.allow_untrusted_root");
        Assert.Contains(symbols, symbol => symbol.Name == "true" && symbol.SubKind == "nuget.allow_untrusted_root");
    }

    [Fact]
    public void Extract_NonNuGetXml_DoesNotPromoteMatchingAttributeValues_Issue4459()
    {
        const string content = """
            <root>
              <packageSources>
                <add key="not-a-source" value="https://example.invalid" />
              </packageSources>
            </root>
            """;

        var symbols = SymbolExtractor.Extract(1, "xml", content);

        Assert.DoesNotContain(symbols, symbol => symbol.SubKind?.StartsWith("nuget.", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Extract_RunSettings_EmitsConfigurationSectionsAndValueSignatures_Issue4457()
    {
        const string content = """
            <RunSettings>
              <RunConfiguration>
                <ResultsDirectory>./TestResults</ResultsDirectory>
                <TestSessionTimeout>2700000</TestSessionTimeout>
              </RunConfiguration>
              <xUnit>
                <LongRunningTestSeconds>60</LongRunningTestSeconds>
              </xUnit>
            </RunSettings>
            """;

        var symbols = SymbolExtractor.Extract(1, "xml", content);

        Assert.Contains(symbols, symbol =>
            symbol.Name == "RunSettings.RunConfiguration"
            && symbol.Kind == "namespace"
            && symbol.ContainerName == "RunSettings");
        Assert.Contains(symbols, symbol =>
            symbol.Name == "RunSettings.RunConfiguration.ResultsDirectory"
            && symbol.Signature == "<ResultsDirectory>./TestResults</ResultsDirectory>");
        Assert.Contains(symbols, symbol =>
            symbol.Name == "RunSettings.xUnit.LongRunningTestSeconds"
            && symbol.Signature == "<LongRunningTestSeconds>60</LongRunningTestSeconds>");
    }

    [Fact]
    public void Extract_GenericXml_EmitsBoundedElementAndAttributePaths_Issue4419()
    {
        const string content = """
            <configuration xmlns="urn:sample" mode="strict">
              <packageSources>
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
            </configuration>
            """;

        var symbols = SymbolExtractor.Extract(1, "xml", content);

        var root = Assert.Single(symbols, symbol => symbol.Name == "configuration");
        Assert.Equal("namespace", root.Kind);
        Assert.Equal(1, root.StartLine);
        Assert.Equal(5, root.EndLine);
        Assert.Contains(symbols, symbol =>
            symbol.Kind == "namespace"
            && symbol.Name == "configuration.packageSources.add"
            && symbol.ContainerName == "configuration.packageSources");
        Assert.Contains(symbols, symbol =>
            symbol.Kind == "property"
            && symbol.Name == "configuration.packageSources.add.@key"
            && symbol.ContainerName == "configuration.packageSources.add");
        Assert.DoesNotContain(symbols, symbol => symbol.Name.Contains("xmlns", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_GenericXml_CapsManyAttributesDuringReaderTraversal_Issue4419()
    {
        var attributes = string.Join(' ', Enumerable.Range(0, SymbolExtractor.StructuredDataMaxSymbols + 1).Select(index => $"a{index}=\"v\""));
        var symbols = SymbolExtractor.Extract(1, "xml", $"<configuration {attributes} />");

        Assert.True(symbols.Count <= SymbolExtractor.StructuredDataMaxSymbols + 1);
        Assert.Contains(symbols, symbol =>
            symbol.Kind == "extraction_diagnostic"
            && symbol.Name == "structured_data_xml_symbol_budget_exceeded");
    }

    [Fact]
    public void Extract_GenericXml_BoundsSignaturesBeforeMaterializingLongSharedLine_Issue4419()
    {
        var elements = string.Concat(Enumerable.Range(0, 128).Select(index => $"<value id=\"{index}\" />"));
        var content = "<configuration>" + new string(' ', 16 * 1024) + elements + "</configuration>";

        var symbols = SymbolExtractor.Extract(1, "xml", content);

        Assert.NotEmpty(symbols);
        Assert.All(symbols, symbol => Assert.True(symbol.Signature == null || symbol.Signature.Length <= SymbolExtractor.StructuredDataMaxSignatureLength));
    }

    [Fact]
    public void Extract_XmlBroadXaml_EmitsStructuredDataTruncationDiagnostic_Issue3765()
    {
        var elements = string.Join('\n', Enumerable.Range(0, SymbolExtractor.StructuredDataMaxSymbols + 1).Select(index => $"""  <Button x:Name="Button{index}" />"""));
        var content = $$"""
            <Window x:Class="App.MainWindow" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
            {{elements}}
            </Window>
            """;

        var symbols = SymbolExtractor.Extract(1, "xml", content);

        Assert.Contains(symbols, symbol => symbol.Kind == "extraction_diagnostic" && symbol.Name == "structured_data_xml_symbol_budget_exceeded");
        Assert.True(symbols.Count <= SymbolExtractor.StructuredDataMaxSymbols + 1);
    }

    [Fact]
    public void Extract_AppManifest_IndexesRelevantEntries_Issue3662()
    {
        const string content = """
            <?xml version="1.0" encoding="utf-8"?>
            <assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
              <assemblyIdentity version="1.0.0.0" name="CodeIndex.App" processorArchitecture="*" type="win32" />
              <trustInfo xmlns="urn:schemas-microsoft-com:asm.v2">
                <security>
                  <requestedPrivileges xmlns="urn:schemas-microsoft-com:asm.v3">
                    <requestedExecutionLevel level="asInvoker" uiAccess="false" />
                  </requestedPrivileges>
                </security>
              </trustInfo>
              <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
                <application>
                  <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}" />
                </application>
              </compatibility>
              <application xmlns="urn:schemas-microsoft-com:asm.v3">
                <windowsSettings>
                  <longPathAware>true</longPathAware>
                </windowsSettings>
              </application>
            </assembly>
            """;

        var symbols = SymbolExtractor.Extract(1, "app_manifest", content);

        Assert.Contains(symbols, symbol =>
            symbol.Kind == "assembly"
            && symbol.Name == "CodeIndex.App"
            && symbol.Line == 3);
        Assert.Contains(symbols, symbol => symbol.Name == "assembly.assemblyIdentity.@version");
        Assert.Contains(symbols, symbol => symbol.Name.EndsWith("requestedExecutionLevel.@level", StringComparison.Ordinal));
        Assert.Contains(symbols, symbol => symbol.Name.EndsWith("requestedExecutionLevel.@uiAccess", StringComparison.Ordinal));
        Assert.Contains(symbols, symbol => symbol.Name.EndsWith("supportedOS.{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}", StringComparison.Ordinal));
        Assert.Contains(symbols, symbol =>
            symbol.Kind == "property"
            && symbol.Name == "assembly.application.windowsSettings.longPathAware"
            && symbol.ContainerName == "assembly.application.windowsSettings");
        Assert.Contains(symbols, symbol =>
            symbol.Kind == "namespace"
            && symbol.Name == "assembly.application.windowsSettings"
            && symbol.ContainerName == "assembly.application");
    }

    [Theory]
    [InlineData(DtdProcessing.Prohibit)]
    [InlineData(DtdProcessing.Ignore)]
    public void CreateExtractionXmlReaderSettings_UsesSharedLimits_Issue3981(DtdProcessing dtdProcessing)
    {
        var settings = SymbolExtractor.CreateExtractionXmlReaderSettings(dtdProcessing);

        Assert.Equal(dtdProcessing, settings.DtdProcessing);
        Assert.True(settings.IgnoreComments);
        Assert.True(settings.IgnoreProcessingInstructions);
        Assert.Equal(SymbolExtractor.XmlExtractionMaxCharactersInDocument, settings.MaxCharactersInDocument);
        Assert.Equal(SymbolExtractor.XmlExtractionMaxCharactersFromEntities, settings.MaxCharactersFromEntities);
    }

    [Fact]
    public void CreateExtractionXmlReaderSettings_RejectsDtdParsing_Issue4345()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            SymbolExtractor.CreateExtractionXmlReaderSettings(DtdProcessing.Parse));

        Assert.Equal("dtdProcessing", exception.ParamName);
    }

    [Fact]
    public void Extract_AppManifest_IgnoresDtdWithSharedReaderPolicy_Issue3981()
    {
        const string content = """
            <!DOCTYPE assembly [
              <!ENTITY local "ignored">
            ]>
            <assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
              <assemblyIdentity version="1.0.0.0" name="CodeIndex.App" processorArchitecture="*" type="win32" />
            </assembly>
            """;

        var symbols = SymbolExtractor.Extract(1, "app_manifest", content);

        Assert.Contains(symbols, symbol =>
            symbol.Kind == "assembly"
            && symbol.Name == "CodeIndex.App"
            && symbol.Line == 5);
    }

    [Fact]
    public void Extract_AppManifest_DoesNotResolveExternalEntityWithSharedReaderPolicy_Issue4345()
    {
        const string content = """
            <!DOCTYPE assembly [
              <!ENTITY xxe SYSTEM "file:///should/not/be/read">
            ]>
            <assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
              <assemblyIdentity version="1.0.0.0" name="CodeIndex.App" processorArchitecture="*" type="win32" />
              <description>&xxe;</description>
            </assembly>
            """;

        var symbols = SymbolExtractor.Extract(1, "app_manifest", content);

        Assert.Contains(symbols, symbol =>
            symbol.Kind == "assembly"
            && symbol.Name == "CodeIndex.App");
        Assert.DoesNotContain(symbols, symbol => symbol.Signature?.Contains("should/not/be/read", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void TryGetXmlStructureIssue_DtdDetectionDoesNotUseExceptionMessage_Issue3981()
    {
        const string content = """
            <?xml version="1.0"?>
            <!-- <!DOCTYPE ignored> -->
            <!DOCTYPE root [
              <!ENTITY injected "value">
            ]>
            <root />
            """;

        Assert.True(SymbolExtractor.TryGetXmlStructureIssue(content, out var issue));
        Assert.Equal("xml_dtd_prohibited", issue.Kind);
        Assert.Equal(3, issue.Line);
    }

    [Fact]
    public void TryGetXmlStructureIssue_DocumentCharactersBeyondLimitEmitsBudgetIssue_Issue3981()
    {
        var content = "<root>" + new string('a', (int)SymbolExtractor.XmlExtractionMaxCharactersInDocument) + "</root>";

        Assert.True(SymbolExtractor.TryGetXmlStructureIssue(content, out var issue));
        Assert.Equal("xml_structure_budget_exceeded", issue.Kind);
        Assert.Equal(1, issue.Line);
        Assert.Contains("document length", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_Xml_XamlCapturesXClassAndXName()
    {
        var content = """
            <ContentPage x:Class="Sample.MainWindow"
                    xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
                    xmlns:x = "http://schemas.microsoft.com/winfx/2009/xaml">
                <Grid>
                    <Button x:Name="SaveButton" Content="Save" />
                    <TextBlock x:Name="StatusText" />
                </Grid>
            </ContentPage>
            """;

        var symbols = SymbolExtractor.Extract(1, "xml", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Sample.MainWindow");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "SaveButton");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "StatusText");
    }

    [Fact]
    public void Extract_Xml_CapsDeepXamlElementTrees_Issue3801()
    {
        var depth = SymbolExtractor.XmlExtractionMaxDepth + 4;
        var content = "<ContentPage x:Class=\"Sample.MainWindow\" "
            + "xmlns=\"http://schemas.microsoft.com/dotnet/2021/maui\" "
            + "xmlns:x=\"http://schemas.microsoft.com/winfx/2009/xaml\">"
            + string.Concat(Enumerable.Repeat("<Grid x:Name=\"NestedGrid\">", depth))
            + string.Concat(Enumerable.Repeat("</Grid>", depth))
            + "</ContentPage>";

        var symbols = SymbolExtractor.Extract(1, "xml", content);

        Assert.Empty(symbols);
    }

    [Fact]
    public void Extract_Xml_ProhibitsDtdDeclarations_Issue3801()
    {
        const string content = """
            <!DOCTYPE ContentPage [
              <!ENTITY injected "value">
            ]>
            <ContentPage x:Class="Sample.MainWindow"
                    xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml">
                <Button x:Name="SaveButton" />
            </ContentPage>
            """;

        var symbols = SymbolExtractor.Extract(1, "xml", content);

        Assert.Empty(symbols);
    }

    [Fact]
    public void Extract_MsBuild_MalformedXmlReturnsBoundedPartialSymbols_Issue4345()
    {
        const string content = """
            <Project>
              <Target Name="Build">
            </Project>
            """;

        var exception = Record.Exception(() => SymbolExtractor.Extract(1, "msbuild", content));
        var symbols = SymbolExtractor.Extract(1, "msbuild", content);

        Assert.Null(exception);
        Assert.Contains(symbols, symbol => symbol.Kind == "function" && symbol.Name == "Build");
        Assert.DoesNotContain(symbols, symbol => symbol.Signature?.Contains("</Project>", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Extract_Xml_XamlCapturesTargetTypeAndDataType()
    {
        var content = """
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                xmlns:vm="clr-namespace:Sample.ViewModels">
                <Style TargetType="Button">
                    <Setter Property="Background" Value="Tomato" />
                </Style>
                <ControlTemplate TargetType="{x:Type vm:CustomButton}">
                    <Grid />
                </ControlTemplate>
                <DataTemplate x:DataType="vm:PersonViewModel">
                    <TextBlock Text="{Binding FullName}" />
                </DataTemplate>
            </ResourceDictionary>
            """;

        var symbols = SymbolExtractor.Extract(1, "xml", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Button");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "vm:CustomButton");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "vm:PersonViewModel");
    }

    [Fact]
    public void Extract_Xml_XamlCapturesTypeArgumentVariantsAsClassSymbols()
    {
        var content = """
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                xmlns:vm="clr-namespace:Sample.ViewModels"
                                xmlns:local="clr-namespace:Sample.Controls">
                <local:Pair x:TypeArguments="x:String, vm:PersonViewModel" />
                <local:Factory x:TypeArguments="{x:Type vm:CustomButton}" />
                <local:Nested x:TypeArguments="vm:Outer(x:String, vm:InnerModel)" />
                <local:Pair
                    x:TypeArguments="vm:Wrapped,
                                     vm:Nested(
                                         vm:WrappedInner,
                                         x:Int32)" />
            </ResourceDictionary>
            """;

        var symbols = SymbolExtractor.Extract(1, "xml", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "x:String");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "vm:Outer");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "vm:InnerModel");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "vm:PersonViewModel");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "vm:CustomButton");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "vm:Wrapped");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "vm:Nested");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "vm:WrappedInner");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "x:Int32");
    }

    [Fact]
    public void Extract_Xml_XamlCapturesWrappedTypeBearingAttributesAcrossLines()
    {
        var content = """
            <Window
                x:Class=
                    "Sample.MainWindow"
                xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                xmlns:vm="clr-namespace:Sample.ViewModels">
                <DataTemplate
                    x:DataType=
                        "vm:PersonViewModel">
                    <Style
                        TargetType=
                            "{x:Type vm:CustomButton}" />
                </DataTemplate>
            </Window>
            """;

        var symbols = SymbolExtractor.Extract(1, "xml", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "Sample.MainWindow");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "vm:PersonViewModel");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "vm:CustomButton");
    }

    [Fact]
    public void Extract_Xml_XamlCapturesTypeObjectPropertyAndMarkupForms()
    {
        var content = """
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                xmlns:vm="clr-namespace:Sample.ViewModels">
                <DataTemplate.DataType>
                    <x:Type TypeName=
                        "vm:PersonViewModel" />
                </DataTemplate.DataType>
                <Style.TargetType>
                    <x:TypeExtension TypeName=
                        "{x:Type vm:CustomButton}" />
                </Style.TargetType>
                <x:Type.TypeName>
                    vm:PropertyPerson
                </x:Type.TypeName>
                <x:TypeExtension.TypeName>
                    {x:Type vm:PropertyButton}
                </x:TypeExtension.TypeName>
                <ControlTemplate TargetType="{x:Type vm:MarkupPerson}" />
                <TextBlock ToolTip="{x:TypeExtension TypeName=vm:MarkupButton}" />
            </ResourceDictionary>
            """;

        var symbols = SymbolExtractor.Extract(1, "xml", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "vm:PersonViewModel");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "vm:CustomButton");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "vm:PropertyPerson");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "vm:PropertyButton");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "vm:MarkupPerson");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "vm:MarkupButton");
    }

    [Fact]
    public void Extract_Xml_XamlCapturesXStaticMemberTypeReferences()
    {
        var content = """
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                xmlns:local="clr-namespace:Sample.ViewModels">
                <SolidColorBrush x:Key="{x:Static local:Keys.AccentBrush}" Color="Tomato" />
                <TextBlock Text="{x:Static local:App.DisplayName}" />
                <Style x:Key="{x:Static Member={x:Type local:Keys}.PrimaryStyleKey}">
                    <Setter Property="Background" Value="Tomato" />
                </Style>
            </ResourceDictionary>
            """;

        var symbols = SymbolExtractor.Extract(1, "xml", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "local:Keys");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "local:App");
    }

    [Fact]
    public void Extract_Xml_XamlCapturesWrappedSearchAttributesAcrossLines()
    {
        var content = """
            <ContentPage xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         xmlns:local="clr-namespace:Sample.ViewModels">
                <ContentPage.Resources>
                    <SolidColorBrush
                        x:Key=
                            "{x:Static Member={x:Type local:Keys}.AccentBrush}"
                        Color="Tomato" />
                </ContentPage.Resources>
                <VerticalStackLayout>
                    <Button
                        x:Name=
                            "SaveButton"
                        Clicked=
                            "OnSaveClicked" />
                    <Entry
                        TextChanged=
                            "OnFilterTextChanged" />
                    <CollectionView SelectionChanged="OnSelectionChanged" />
                </VerticalStackLayout>
            </ContentPage>
            """;

        var symbols = SymbolExtractor.Extract(1, "xml", content);

        Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "local:Keys.AccentBrush"));
        Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "SaveButton"));
        Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "OnSaveClicked"));
        Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "OnFilterTextChanged"));
        Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "OnSelectionChanged"));
    }

    [Fact]
    public void Extract_Xml_XamlCapturesBindingPathVariants()
    {
        var content = """
            <ContentPage xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         xmlns:vm="clr-namespace:Sample.ViewModels">
                <StackPanel DataContext="{Binding Source=Root, Path=ViewModel}">
                    <Label Text="{Binding
                        Title}" />
                    <Button Command="{x:Bind
                        ViewModel.SaveCommand}" />
                    <TextBlock Text="{CompiledBinding CompiledModel.CompiledTitle}" />
                    <TextBox Text="{ReflectionBinding Path=Search.FilterText}" />
                    <Button Command="{CompiledBinding Commands.CompiledSave}" />
                    <TextBlock Tag="{CompiledBinding Path=Profile.DisplayName, ConverterParameter='Path=Ignored'}" />
                </StackPanel>
            </ContentPage>
            """;

        var symbols = SymbolExtractor.Extract(1, "xml", content);

        var propertyNames = symbols.Where(s => s.Kind == "property").Select(s => s.Name).ToList();

        Assert.Contains("ViewModel", propertyNames);
        Assert.Contains("Title", propertyNames);
        Assert.Contains("SaveCommand", propertyNames);
        Assert.Contains("CompiledTitle", propertyNames);
        Assert.Contains("FilterText", propertyNames);
        Assert.Contains("CompiledSave", propertyNames);
        Assert.Contains("DisplayName", propertyNames);
        Assert.DoesNotContain("Ignored", propertyNames);
        Assert.DoesNotContain("CompiledModel", propertyNames);
        Assert.DoesNotContain("Root", propertyNames);
    }

    [Fact]
    public void Extract_Xml_XamlCapturesNamedObjectReferenceVariants()
    {
        var content = """
            <ContentPage xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         xmlns:vm="clr-namespace:Sample.ViewModels">
                <Grid>
                    <TextBlock Text="{Binding Text, ElementName=SearchBox}" />
                    <Slider Value="{Binding ElementName=VolumeSlider, Path=Value}" />
                    <TextBlock Tag="{Binding Path=Title, ConverterParameter='prefix, ElementName=Ignored'}" />
                    <Binding
                        ElementName="RootPanel"
                        Path="DataContext.CurrentUser.Name" />
                    <Binding.ElementName>
                        DetailsList
                    </Binding.ElementName>
                    <TextBlock Text="{Binding Source={x:Reference ReferenceRoot}, Path=ReferenceTitle}" />
                    <TextBlock Text="{Binding Source={x:Reference Name=NamedTarget}, Path=ReferenceTitle}" />
                    <TextBlock Text="{Binding Source={x:ReferenceExtension Name=ExtensionTarget}, Path=ReferenceTitle}" />
                    <x:Reference ToolTip="Name='Ignored'" Name="ObjectTarget" />
                    <x:Reference.Name>
                        PropertyTarget
                    </x:Reference.Name>
                </Grid>
            </ContentPage>
            """;

        var symbols = SymbolExtractor.Extract(1, "xml", content);
        var propertyNames = symbols.Where(s => s.Kind == "property").Select(s => s.Name).ToList();

        Assert.Contains("SearchBox", propertyNames);
        Assert.Contains("VolumeSlider", propertyNames);
        Assert.Contains("RootPanel", propertyNames);
        Assert.Contains("DetailsList", propertyNames);
        Assert.Contains("Name", propertyNames);
        Assert.Contains("ReferenceRoot", propertyNames);
        Assert.Contains("NamedTarget", propertyNames);
        Assert.Contains("ExtensionTarget", propertyNames);
        Assert.Contains("ObjectTarget", propertyNames);
        Assert.Contains("PropertyTarget", propertyNames);
        Assert.Contains("ReferenceTitle", propertyNames);
        Assert.DoesNotContain("Ignored", propertyNames);
        Assert.DoesNotContain("x:Reference", propertyNames);
    }

    [Fact]
    public void Extract_Xml_XamlCapturesObjectElementBindingPaths()
    {
        var content = """
            <ContentPage xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         xmlns:vm="clr-namespace:Sample.ViewModels">
                <TextBlock>
                    <TextBlock.Text>
                        <MultiBinding StringFormat="{}{0} {1}">
                            <Binding
                                Source="Root"
                                ConverterParameter="Path='Ignored'"
                                Path="ViewModel.FirstName" />
                            <Binding Path="vm:PersonViewModel.LastName" />
                        </MultiBinding>
                    </TextBlock.Text>
                </TextBlock>
                <Binding.Path>
                    Profile.DisplayName
                </Binding.Path>
            </ContentPage>
            """;

        var symbols = SymbolExtractor.Extract(1, "xml", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "FirstName");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "LastName");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "DisplayName");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "Root");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "Ignored");
    }

    [Fact]
    public void Extract_Xml_XamlCapturesTemplateBindingProperties()
    {
        var content = """
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                xmlns:local="clr-namespace:Sample.Controls">
                <ControlTemplate TargetType="{x:Type local:ButtonChrome}">
                    <Border Background="{TemplateBinding Background}"
                            BorderBrush="{TemplateBinding Property=local:ButtonChrome.BorderBrush}" />
                </ControlTemplate>
            </ResourceDictionary>
            """;

        var symbols = SymbolExtractor.Extract(1, "xml", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Background");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "BorderBrush");
    }

    [Fact]
    public void Extract_Xml_XamlCapturesStaticAndDynamicResourceKeys()
    {
        var content = """
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                xmlns:local="clr-namespace:Sample.ViewModels">
                <SolidColorBrush x:Key="PrimaryBrush" Color="Tomato" />
                <SolidColorBrush x:Key="{x:Static local:Keys.WarningBrush}" Color="Orange" />
                <SolidColorBrush x:Key="{x:Static Member={x:Type local:Keys}.AccentBrush}" Color="Red" />
                <Style x:Key="PrimaryButtonStyle" TargetType="Button" />
                <TextBlock Foreground="{StaticResource PrimaryBrush}" />
                <Border BorderBrush="{DynamicResource ResourceKey={x:Static Member={x:Type local:Keys}.AccentBrush}}" />
                <TextBlock DataContext="{Binding Source={StaticResource ViewModelLocator}, Path=CurrentUser.DisplayName}" />
                <TextBlock ToolTip="{StaticResource}" />
                <Border Background="{DynamicResource ResourceKey=}" />
            </ResourceDictionary>
            """;

        var symbols = SymbolExtractor.Extract(1, "xml", content);
        var propertyNames = symbols.Where(s => s.Kind == "property").Select(s => s.Name).ToList();

        Assert.Contains("PrimaryBrush", propertyNames);
        Assert.Contains("local:Keys.WarningBrush", propertyNames);
        Assert.Contains("local:Keys.AccentBrush", propertyNames);
        Assert.Contains("PrimaryButtonStyle", propertyNames);
        Assert.Contains("ViewModelLocator", propertyNames);
        Assert.Contains("DisplayName", propertyNames);
        Assert.DoesNotContain("StaticResource", propertyNames);
        Assert.DoesNotContain("DynamicResource", propertyNames);
        Assert.DoesNotContain("ResourceKey", propertyNames);
    }

    [Fact]
    public void Extract_Xml_NonXamlXmlDoesNotEmitXamlSymbols()
    {
        var content = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <Foo x:Name="ShouldNotBeCaptured" />
              </ItemGroup>
            </Project>
            """;

        var symbols = SymbolExtractor.Extract(1, "xml", content);
        Assert.DoesNotContain(symbols, s => s.Name == "ShouldNotBeCaptured");
    }
}
