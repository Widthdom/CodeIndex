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
    public void Extract_XmlBroadXaml_EmitsStructuredDataTruncationDiagnostic_Issue3765()
    {
        var elements = string.Join('\n', Enumerable.Range(0, SymbolExtractor.StructuredDataMaxSymbols + 5).Select(index => $"""  <Button x:Name="Button{index}" />"""));
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
        Assert.Contains(symbols, symbol => symbol.Name == "assemblyIdentity.version");
        Assert.Contains(symbols, symbol => symbol.Name == "requestedExecutionLevel.level");
        Assert.Contains(symbols, symbol => symbol.Name == "requestedExecutionLevel.uiAccess");
        Assert.Contains(symbols, symbol => symbol.Name == "supportedOS.{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}");
        Assert.Contains(symbols, symbol => symbol.Name == "longPathAware");
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
    public void Extract_Xml_XamlCapturesXKey()
    {
        var content = """
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <SolidColorBrush x:Key="{x:Static Member={x:Type local:Keys}.AccentBrush}" Color="Tomato" />
                <Style x:Key="PrimaryButtonStyle" TargetType="Button">
                    <Setter Property="Background" Value="{StaticResource AccentBrush}" />
                </Style>
            </ResourceDictionary>
            """;

        var symbols = SymbolExtractor.Extract(1, "xml", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "local:Keys.AccentBrush");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "PrimaryButtonStyle");
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
    public void Extract_Xml_XamlCapturesTypeArgumentsAsClassSymbols()
    {
        var content = """
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                xmlns:vm="clr-namespace:Sample.ViewModels"
                                xmlns:local="clr-namespace:Sample.Controls">
                <local:Pair x:TypeArguments="x:String, vm:PersonViewModel" />
                <local:Factory x:TypeArguments="{x:Type vm:CustomButton}" />
                <local:Nested x:TypeArguments="vm:Outer(x:String, vm:InnerModel)" />
            </ResourceDictionary>
            """;

        var symbols = SymbolExtractor.Extract(1, "xml", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "x:String");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "vm:PersonViewModel");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "vm:CustomButton");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "vm:Outer");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "vm:InnerModel");
    }

    [Fact]
    public void Extract_Xml_XamlCapturesWrappedTypeArgumentsAcrossLines()
    {
        var content = """
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                xmlns:vm="clr-namespace:Sample.ViewModels"
                                xmlns:local="clr-namespace:Sample.Controls">
                <local:Pair
                    x:TypeArguments="x:String,
                                     vm:Outer(
                                         vm:InnerModel,
                                         x:Int32)" />
            </ResourceDictionary>
            """;

        var symbols = SymbolExtractor.Extract(1, "xml", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "x:String");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "vm:Outer");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "vm:InnerModel");
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
    public void Extract_Xml_XamlCapturesTypeObjectElementsAcrossLines()
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
            </ResourceDictionary>
            """;

        var symbols = SymbolExtractor.Extract(1, "xml", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "vm:PersonViewModel");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "vm:CustomButton");
    }

    [Fact]
    public void Extract_Xml_XamlCapturesTypePropertyElementsAcrossLines()
    {
        var content = """
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                xmlns:vm="clr-namespace:Sample.ViewModels">
                <x:Type.TypeName>
                    vm:PersonViewModel
                </x:Type.TypeName>
                <x:TypeExtension.TypeName>
                    {x:Type vm:CustomButton}
                </x:TypeExtension.TypeName>
            </ResourceDictionary>
            """;

        var symbols = SymbolExtractor.Extract(1, "xml", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "vm:PersonViewModel");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "vm:CustomButton");
    }

    [Fact]
    public void Extract_Xml_XamlCapturesTypeMarkupExtensions()
    {
        var content = """
            <ContentPage xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         xmlns:vm="clr-namespace:Sample.ViewModels">
                <ContentPage.Resources>
                    <ControlTemplate TargetType="{x:Type vm:PersonViewModel}" />
                    <TextBlock ToolTip="{x:TypeExtension TypeName=vm:CustomButton}" />
                </ContentPage.Resources>
            </ContentPage>
            """;

        var symbols = SymbolExtractor.Extract(1, "xml", content);

        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "vm:PersonViewModel");
        Assert.Contains(symbols, s => s.Kind == "class" && s.Name == "vm:CustomButton");
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
    public void Extract_Xml_XamlCapturesCommonEventHandlers()
    {
        var content = """
            <ContentPage xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <VerticalStackLayout>
                    <Button Text="Save" Clicked="OnSaveClicked" />
                    <Entry TextChanged="OnFilterTextChanged" />
                    <CollectionView SelectionChanged="OnSelectionChanged" />
                </VerticalStackLayout>
            </ContentPage>
            """;

        var symbols = SymbolExtractor.Extract(1, "xml", content);

        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "OnSaveClicked");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "OnFilterTextChanged");
        Assert.Contains(symbols, s => s.Kind == "function" && s.Name == "OnSelectionChanged");
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
                </VerticalStackLayout>
            </ContentPage>
            """;

        var symbols = SymbolExtractor.Extract(1, "xml", content);

        Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "local:Keys.AccentBrush"));
        Assert.Single(symbols.Where(s => s.Kind == "property" && s.Name == "SaveButton"));
        Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "OnSaveClicked"));
        Assert.Single(symbols.Where(s => s.Kind == "function" && s.Name == "OnFilterTextChanged"));
    }

    [Fact]
    public void Extract_Xml_XamlCapturesBindingPaths()
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
                </StackPanel>
            </ContentPage>
            """;

        var symbols = SymbolExtractor.Extract(1, "xml", content);

        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "ViewModel");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "Title");
        Assert.Contains(symbols, s => s.Kind == "property" && s.Name == "SaveCommand");
        Assert.DoesNotContain(symbols, s => s.Kind == "property" && s.Name == "Root");
    }

    [Fact]
    public void Extract_Xml_XamlCapturesCompiledAndReflectionBindingPaths()
    {
        var content = """
            <Window xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <StackPanel>
                    <TextBlock Text="{CompiledBinding ViewModel.Title}" />
                    <TextBox Text="{ReflectionBinding Path=Search.FilterText}" />
                    <Button Command="{CompiledBinding
                        Commands.Save}" />
                    <TextBlock Tag="{CompiledBinding Path=Profile.DisplayName, ConverterParameter='Path=Ignored'}" />
                </StackPanel>
            </Window>
            """;

        var symbols = SymbolExtractor.Extract(1, "xml", content);
        var propertyNames = symbols.Where(s => s.Kind == "property").Select(s => s.Name).ToList();

        Assert.Contains("Title", propertyNames);
        Assert.Contains("FilterText", propertyNames);
        Assert.Contains("Save", propertyNames);
        Assert.Contains("DisplayName", propertyNames);
        Assert.DoesNotContain("Ignored", propertyNames);
        Assert.DoesNotContain("ViewModel", propertyNames);
    }

    [Fact]
    public void Extract_Xml_XamlCapturesBindingElementNameReferences()
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
        Assert.DoesNotContain("Ignored", propertyNames);
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
    public void Extract_Xml_XamlCapturesXReferenceTargets()
    {
        var content = """
            <ContentPage xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         xmlns:local="clr-namespace:Sample.ViewModels">
                <Grid>
                    <TextBlock Text="{Binding Source={x:Reference RootPanel}, Path=Title}" />
                    <TextBlock Text="{Binding Source={x:Reference Name=NamedTarget}, Path=Title}" />
                    <TextBlock Text="{Binding Source={x:ReferenceExtension Name=ExtensionTarget}, Path=Title}" />
                    <x:Reference ToolTip="Name='Ignored'" Name="ObjectTarget" />
                    <x:Reference.Name>
                        PropertyTarget
                    </x:Reference.Name>
                </Grid>
            </ContentPage>
            """;

        var symbols = SymbolExtractor.Extract(1, "xml", content);
        var propertyNames = symbols.Where(s => s.Kind == "property").Select(s => s.Name).ToList();

        Assert.Contains("RootPanel", propertyNames);
        Assert.Contains("NamedTarget", propertyNames);
        Assert.Contains("ExtensionTarget", propertyNames);
        Assert.Contains("ObjectTarget", propertyNames);
        Assert.Contains("PropertyTarget", propertyNames);
        Assert.Contains("Title", propertyNames);
        Assert.DoesNotContain("Ignored", propertyNames);
        Assert.DoesNotContain("x:Reference", propertyNames);
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
