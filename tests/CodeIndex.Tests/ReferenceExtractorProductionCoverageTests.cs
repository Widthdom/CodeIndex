using CodeIndex.Indexer;
using CodeIndex.Models;

namespace CodeIndex.Tests;

public class SwiftReferenceExtractorTests
{
    [Fact]
    public void Extract_Swift_ProductionCoverage_HasExpectedPlacements()
    {
        var references = ReferenceCoverage.Extract("swift", """
            func login() {
                authenticate()
            }
            func run(items: [Item]) {
                ServiceFactory.shared.makeClient()
                items.publisher().compactMap(transform).sink(receiveValue: save)
            }
            func handle(value: Payload) -> ResultWrapper {
                let model: UserModel = load()
                if model is PremiumUser {
                    publish(model)
                }
                return ResultWrapper()
            }
            func declaredOnly() {}
            // ignoredCall()
            let value = "fakeCall()"
            """);

        ReferenceCoverage.AssertPlacement(references, "authenticate", "call", 2, "authenticate()", "function", "login");
        ReferenceCoverage.AssertPlacement(references, "makeClient", "call", 5, "ServiceFactory.shared.makeClient()", "function", "run");
        ReferenceCoverage.AssertPlacement(references, "publisher", "call", 6, "items.publisher().compactMap(transform).sink(receiveValue: save)", "function", "run");
        ReferenceCoverage.AssertPlacement(references, "compactMap", "call", 6, "items.publisher().compactMap(transform).sink(receiveValue: save)", "function", "run");
        ReferenceCoverage.AssertPlacement(references, "sink", "call", 6, "items.publisher().compactMap(transform).sink(receiveValue: save)", "function", "run");
        ReferenceCoverage.AssertPlacement(references, "Payload", "type_reference", 8, "func handle(value: Payload) -> ResultWrapper {", "function", "handle");
        ReferenceCoverage.AssertPlacement(references, "ResultWrapper", "type_reference", 8, "func handle(value: Payload) -> ResultWrapper {", "function", "handle");
        ReferenceCoverage.AssertPlacement(references, "UserModel", "type_reference", 9, "let model: UserModel = load()", "function", "handle");
        ReferenceCoverage.AssertPlacement(references, "PremiumUser", "type_reference", 10, "if model is PremiumUser {", "function", "handle");

        ReferenceCoverage.AssertAbsent(references, "declaredOnly", "call");
        ReferenceCoverage.AssertAbsent(references, "ignoredCall");
        ReferenceCoverage.AssertAbsent(references, "fakeCall");
    }
}

public class ObjectiveCReferenceExtractorTests
{
    [Fact]
    public void Extract_ObjectiveC_ProductionCoverage_HasExpectedPlacements()
    {
        var references = ReferenceCoverage.Extract("objc", """
            void Run(void) {
                CFRelease(token);
                id client = [HTTPClient sharedClient];
                id request = [client requestBuilder];
                [request send];
            }
            @interface Controller : BaseController <ControllerDelegate>
            @property (nonatomic, strong) UserModel *model;
            - (Result *)handle:(Payload *)payload;
            @end
            @interface Service
            - (void)declaredOnly;
            @end
            // [Service ignoredCall];
            NSString *text = @"fakeCall()";
            """);

        ReferenceCoverage.AssertPlacement(references, "CFRelease", "call", 2, "CFRelease(token);", null, null);
        ReferenceCoverage.AssertPlacement(references, "sharedClient", "call", 3, "id client = [HTTPClient sharedClient];", null, null);
        ReferenceCoverage.AssertPlacement(references, "requestBuilder", "call", 4, "id request = [client requestBuilder];", null, null);
        ReferenceCoverage.AssertPlacement(references, "send", "call", 5, "[request send];", null, null);
        ReferenceCoverage.AssertPlacement(references, "BaseController", "type_reference", 7, "@interface Controller : BaseController <ControllerDelegate>", null, null);
        ReferenceCoverage.AssertPlacement(references, "ControllerDelegate", "type_reference", 7, "@interface Controller : BaseController <ControllerDelegate>", null, null);
        ReferenceCoverage.AssertPlacement(references, "UserModel", "type_reference", 8, "@property (nonatomic, strong) UserModel *model;", null, null);

        ReferenceCoverage.AssertAbsent(references, "declaredOnly", "call");
        ReferenceCoverage.AssertAbsent(references, "ignoredCall");
        ReferenceCoverage.AssertAbsent(references, "fakeCall");
    }
}

public class GradleReferenceExtractorTests
{
    [Fact]
    public void Extract_Gradle_ProductionCoverage_HasExpectedPlacements()
    {
        var references = ReferenceCoverage.Extract("gradle", """
            plugins {
                id 'java'
            }
            apply plugin: 'java'
            task buildJar(type: Jar) {
                dependsOn compileJava
            }
            dependencies {
                implementation project(':core')
                configurations.runtimeClasspath.get().files()
            }
            version = '1.0'
            group = 'demo'
            // ignoredCall()
            """);

        ReferenceCoverage.AssertPlacement(references, "plugins", "call", 1, "plugins {", null, null);
        ReferenceCoverage.AssertPlacement(references, "apply", "call", 4, "apply plugin: 'java'", null, null);
        ReferenceCoverage.AssertPlacement(references, "task", "call", 5, "task buildJar(type: Jar) {", "function", "buildJar");
        ReferenceCoverage.AssertPlacement(references, "dependencies", "call", 8, "dependencies {", null, null);
        ReferenceCoverage.AssertPlacement(references, "implementation", "call", 9, "implementation project(':core')", null, null);
        ReferenceCoverage.AssertPlacement(references, "project", "call", 9, "implementation project(':core')", null, null);
        ReferenceCoverage.AssertPlacement(references, "get", "call", 10, "configurations.runtimeClasspath.get().files()", null, null);
        ReferenceCoverage.AssertPlacement(references, "files", "call", 10, "configurations.runtimeClasspath.get().files()", null, null);

        ReferenceCoverage.AssertAbsent(references, "version", "call");
        ReferenceCoverage.AssertAbsent(references, "group", "call");
        ReferenceCoverage.AssertAbsent(references, "ignoredCall");
    }
}

public class TerraformReferenceExtractorTests
{
    [Fact]
    public void Extract_Terraform_ProductionCoverage_HasExpectedPlacements()
    {
        var references = ReferenceCoverage.Extract("terraform", """
            variable "region" {}
            variable "unused_region" {}
            module "network" {
              source = "./network"
            }
            resource "aws_instance" "web" {}
            resource "aws_s3_bucket" "logs" {}
            data "aws_ami" "ubuntu" {}
            output "region_value" {
              value = var.region
            }
            output "subnet" {
              value = module.network.subnet_id
            }
            output "id" {
              value = aws_instance.web.id
            }
            output "ami" {
              value = data.aws_ami.ubuntu.id
            }
            # var.ignored
            output "literal" {
              value = "module.fake"
            }
            """);

        ReferenceCoverage.AssertPlacement(references, "region", "reference", 10, "value = var.region", "function", "region_value");
        ReferenceCoverage.AssertPlacement(references, "network", "reference", 13, "value = module.network.subnet_id", "function", "subnet");
        ReferenceCoverage.AssertPlacement(references, "web", "reference", 16, "value = aws_instance.web.id", "function", "id");
        ReferenceCoverage.AssertPlacement(references, "ubuntu", "reference", 19, "value = data.aws_ami.ubuntu.id", "function", "ami");

        ReferenceCoverage.AssertAbsent(references, "unused_region", "reference");
        ReferenceCoverage.AssertAbsent(references, "logs", "reference");
        ReferenceCoverage.AssertAbsent(references, "ignored");
        ReferenceCoverage.AssertAbsent(references, "fake");
    }
}

public class PowerShellReferenceExtractorTests
{
    [Fact]
    public void Extract_PowerShell_ProductionCoverage_HasExpectedPlacements()
    {
        var references = ReferenceCoverage.Extract("powershell", """
            Write-Host "hello"
            $items | ForEach-Object { Process-One $_ }
            $result = Invoke-RestMethod -Uri $Uri
            $items | Where-Object { $_.Enabled } | Select-Object Name
            # Ignored-Command "ignored"
            if ($count -lt 10) { return }
            $name = "Fake-Call"
            """);

        ReferenceCoverage.AssertPlacement(references, "Write-Host", "call", 1, "Write-Host \"hello\"", "function", "<script>");
        ReferenceCoverage.AssertPlacement(references, "ForEach-Object", "call", 2, "$items | ForEach-Object { Process-One $_ }", "function", "<script>");
        ReferenceCoverage.AssertPlacement(references, "Process-One", "call", 2, "$items | ForEach-Object { Process-One $_ }", "function", "<script>");
        ReferenceCoverage.AssertPlacement(references, "Invoke-RestMethod", "call", 3, "$result = Invoke-RestMethod -Uri $Uri", "function", "<script>");
        ReferenceCoverage.AssertPlacement(references, "Where-Object", "call", 4, "$items | Where-Object { $_.Enabled } | Select-Object Name", "function", "<script>");
        ReferenceCoverage.AssertPlacement(references, "Select-Object", "call", 4, "$items | Where-Object { $_.Enabled } | Select-Object Name", "function", "<script>");

        ReferenceCoverage.AssertAbsent(references, "Ignored-Command");
        ReferenceCoverage.AssertAbsent(references, "lt");
        ReferenceCoverage.AssertAbsent(references, "Fake-Call");
    }
}

public class BatchReferenceExtractorTests
{
    [Fact]
    public void Extract_Batch_ProductionCoverage_HasExpectedPlacements()
    {
        var references = ReferenceCoverage.Extract("batch", """
            goto :Build
            call :RunTests
            goto :Build & call :Package
            if errorlevel 1 goto :Retry
            :Build
            :RunTests
            :Package
            :Retry
            :DeclaredOnly
            rem goto :Ignored
            :: call :Commented
            echo ^& goto :Escaped
            goto :EOF
            """);

        ReferenceCoverage.AssertPlacement(references, "Build", "call", 1, "goto :Build", null, null);
        ReferenceCoverage.AssertPlacement(references, "RunTests", "call", 2, "call :RunTests", null, null);
        ReferenceCoverage.AssertPlacement(references, "Build", "call", 3, "goto :Build & call :Package", null, null);
        ReferenceCoverage.AssertPlacement(references, "Package", "call", 3, "goto :Build & call :Package", null, null);
        ReferenceCoverage.AssertPlacement(references, "Retry", "call", 4, "if errorlevel 1 goto :Retry", null, null);

        ReferenceCoverage.AssertAbsent(references, "DeclaredOnly");
        ReferenceCoverage.AssertAbsent(references, "Ignored");
        ReferenceCoverage.AssertAbsent(references, "Commented");
        ReferenceCoverage.AssertAbsent(references, "Escaped");
        ReferenceCoverage.AssertAbsent(references, "EOF");
    }
}

file static class ReferenceCoverage
{
    public static IReadOnlyList<ReferenceRecord> Extract(string language, string content)
    {
        var symbols = SymbolExtractor.Extract(1, language, content);
        return ReferenceExtractor.Extract(1, language, content, symbols);
    }

    public static void AssertPlacement(
        IReadOnlyCollection<ReferenceRecord> references,
        string symbolName,
        string referenceKind,
        int line,
        string context,
        string? containerKind,
        string? containerName)
    {
        Assert.Contains(
            references,
            reference => reference.SymbolName == symbolName
                && reference.ReferenceKind == referenceKind
                && reference.Line == line
                && reference.Context == context
                && reference.ContainerKind == containerKind
                && reference.ContainerName == containerName);
    }

    public static void AssertAbsent(
        IReadOnlyCollection<ReferenceRecord> references,
        string symbolName,
        string? referenceKind = null)
    {
        Assert.DoesNotContain(
            references,
            reference => reference.SymbolName == symbolName
                && (referenceKind is null || reference.ReferenceKind == referenceKind));
    }
}
