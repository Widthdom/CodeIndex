using CodeIndex.Indexer;
using CodeIndex.Models;

namespace CodeIndex.Tests;

public class SwiftReferenceExtractorTests
{
    [Fact]
    public void Extract_Swift_BasicCall_IsReferenced()
    {
        var references = Extract("""
            func login() {
                authenticate()
            }
            """);

        AssertCall(references, "authenticate");
    }

    [Fact]
    public void Extract_Swift_QualifiedCall_UsesInvokedMemberName()
    {
        var references = Extract("""
            func run() {
                ServiceFactory.shared.makeClient()
            }
            """);

        AssertCall(references, "makeClient");
    }

    [Fact]
    public void Extract_Swift_MethodCallOnChain_IsReferenced()
    {
        var references = Extract("""
            func run(items: [Item]) {
                items.publisher().compactMap(transform).sink(receiveValue: save)
            }
            """);

        AssertCall(references, "publisher");
        AssertCall(references, "compactMap");
        AssertCall(references, "sink");
    }

    [Fact]
    public void Extract_Swift_TypePositions_AreTypeReferences()
    {
        var references = Extract("""
            func handle(value: Payload) -> ResultWrapper {
                let model: UserModel = load()
                if model is PremiumUser {
                    publish(model)
                }
                return ResultWrapper()
            }
            """);

        AssertTypeReference(references, "Payload");
        AssertTypeReference(references, "ResultWrapper");
        AssertTypeReference(references, "UserModel");
        AssertTypeReference(references, "PremiumUser");
    }

    [Fact]
    public void Extract_Swift_CommentsAndDeclarations_DoNotEmitCalls()
    {
        var references = Extract("""
            func declaredOnly() {}
            // ignoredCall()
            let value = "fakeCall()"
            """);

        Assert.DoesNotContain(references, r => r.SymbolName == "declaredOnly" && r.ReferenceKind == "call");
        Assert.DoesNotContain(references, r => r.SymbolName == "ignoredCall");
        Assert.DoesNotContain(references, r => r.SymbolName == "fakeCall");
    }

    private static IReadOnlyList<ReferenceRecord> Extract(string content)
    {
        var symbols = SymbolExtractor.Extract(1, "swift", content);
        return ReferenceExtractor.Extract(1, "swift", content, symbols);
    }

    private static void AssertCall(IReadOnlyCollection<ReferenceRecord> references, string symbolName)
        => Assert.Contains(references, r => r.SymbolName == symbolName && r.ReferenceKind == "call");

    private static void AssertTypeReference(IReadOnlyCollection<ReferenceRecord> references, string symbolName)
        => Assert.Contains(references, r => r.SymbolName == symbolName && r.ReferenceKind == "type_reference");
}

public class ObjectiveCReferenceExtractorTests
{
    [Fact]
    public void Extract_ObjectiveC_CFunctionCall_IsReferenced()
    {
        var references = Extract("""
            void Run(void) {
                CFRelease(token);
            }
            """);

        AssertCall(references, "CFRelease");
    }

    [Fact]
    public void Extract_ObjectiveC_ClassMessage_IsReferenced()
    {
        var references = Extract("""
            void Run(void) {
                id client = [HTTPClient sharedClient];
            }
            """);

        AssertCall(references, "sharedClient");
    }

    [Fact]
    public void Extract_ObjectiveC_ChainedMessage_IsReferenced()
    {
        var references = Extract("""
            void Run(void) {
                id client = [HTTPClient sharedClient];
                id request = [client requestBuilder];
                [request send];
            }
            """);

        AssertCall(references, "sharedClient");
        AssertCall(references, "requestBuilder");
        AssertCall(references, "send");
    }

    [Fact]
    public void Extract_ObjectiveC_TypePositions_AreTypeReferences()
    {
        var references = Extract("""
            @interface Controller : BaseController <ControllerDelegate>
            @property (nonatomic, strong) UserModel *model;
            - (Result *)handle:(Payload *)payload;
            @end
            """);

        AssertTypeReference(references, "BaseController");
        AssertTypeReference(references, "ControllerDelegate");
        AssertTypeReference(references, "UserModel");
    }

    [Fact]
    public void Extract_ObjectiveC_CommentsAndDeclarations_DoNotEmitCalls()
    {
        var references = Extract("""
            @interface Service
            - (void)declaredOnly;
            @end
            // [Service ignoredCall];
            NSString *text = @"fakeCall()";
            """);

        Assert.DoesNotContain(references, r => r.SymbolName == "declaredOnly" && r.ReferenceKind == "call");
        Assert.DoesNotContain(references, r => r.SymbolName == "ignoredCall");
        Assert.DoesNotContain(references, r => r.SymbolName == "fakeCall");
    }

    private static IReadOnlyList<ReferenceRecord> Extract(string content)
    {
        var symbols = SymbolExtractor.Extract(1, "objc", content);
        return ReferenceExtractor.Extract(1, "objc", content, symbols);
    }

    private static void AssertCall(IReadOnlyCollection<ReferenceRecord> references, string symbolName)
        => Assert.Contains(references, r => r.SymbolName == symbolName && r.ReferenceKind == "call");

    private static void AssertTypeReference(IReadOnlyCollection<ReferenceRecord> references, string symbolName)
        => Assert.Contains(references, r => r.SymbolName == symbolName && r.ReferenceKind == "type_reference");
}

public class GradleReferenceExtractorTests
{
    [Fact]
    public void Extract_Gradle_BlockDslCall_IsReferenced()
    {
        var references = Extract("""
            plugins {
                id 'java'
            }
            """);

        AssertCall(references, "plugins");
    }

    [Fact]
    public void Extract_Gradle_CommandDslCall_IsReferenced()
    {
        var references = Extract("""
            apply plugin: 'java'
            """);

        AssertCall(references, "apply");
    }

    [Fact]
    public void Extract_Gradle_TaskWithTypeArgument_IsReferenced()
    {
        var references = Extract("""
            task buildJar(type: Jar) {
                dependsOn compileJava
            }
            """);

        AssertCall(references, "task");
    }

    [Fact]
    public void Extract_Gradle_MethodCallOnChain_IsReferenced()
    {
        var references = Extract("""
            dependencies {
                implementation project(':core')
                configurations.runtimeClasspath.get().files()
            }
            """);

        AssertCall(references, "dependencies");
        AssertCall(references, "implementation");
        AssertCall(references, "project");
        AssertCall(references, "get");
        AssertCall(references, "files");
    }

    [Fact]
    public void Extract_Gradle_AssignmentsAndComments_DoNotEmitCalls()
    {
        var references = Extract("""
            version = '1.0'
            group = 'demo'
            // ignoredCall()
            """);

        Assert.DoesNotContain(references, r => r.SymbolName == "version" && r.ReferenceKind == "call");
        Assert.DoesNotContain(references, r => r.SymbolName == "group" && r.ReferenceKind == "call");
        Assert.DoesNotContain(references, r => r.SymbolName == "ignoredCall");
    }

    private static IReadOnlyList<ReferenceRecord> Extract(string content)
    {
        var symbols = SymbolExtractor.Extract(1, "gradle", content);
        return ReferenceExtractor.Extract(1, "gradle", content, symbols);
    }

    private static void AssertCall(IReadOnlyCollection<ReferenceRecord> references, string symbolName)
        => Assert.Contains(references, r => r.SymbolName == symbolName && r.ReferenceKind == "call");
}

public class TerraformReferenceExtractorTests
{
    [Fact]
    public void Extract_Terraform_VariableReference_IsReferenced()
    {
        var references = Extract("""
            variable "region" {}
            output "region" {
              value = var.region
            }
            """);

        AssertReference(references, "region");
    }

    [Fact]
    public void Extract_Terraform_ModuleReference_IsReferenced()
    {
        var references = Extract("""
            module "network" {
              source = "./network"
            }
            output "subnet" {
              value = module.network.subnet_id
            }
            """);

        AssertReference(references, "network");
    }

    [Fact]
    public void Extract_Terraform_ResourceReference_IsReferenced()
    {
        var references = Extract("""
            resource "aws_instance" "web" {}
            output "id" {
              value = aws_instance.web.id
            }
            """);

        AssertReference(references, "web");
    }

    [Fact]
    public void Extract_Terraform_DataReference_IsReferenced()
    {
        var references = Extract("""
            data "aws_ami" "ubuntu" {}
            output "ami" {
              value = data.aws_ami.ubuntu.id
            }
            """);

        AssertReference(references, "ubuntu");
    }

    [Fact]
    public void Extract_Terraform_DefinitionsAndComments_DoNotEmitReferences()
    {
        var references = Extract("""
            variable "region" {}
            resource "aws_s3_bucket" "logs" {}
            # var.ignored
            output "literal" {
              value = "module.fake"
            }
            """);

        Assert.DoesNotContain(references, r => r.SymbolName == "region" && r.ReferenceKind == "reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "logs" && r.ReferenceKind == "reference");
        Assert.DoesNotContain(references, r => r.SymbolName == "ignored");
        Assert.DoesNotContain(references, r => r.SymbolName == "fake");
    }

    private static IReadOnlyList<ReferenceRecord> Extract(string content)
    {
        var symbols = SymbolExtractor.Extract(1, "terraform", content);
        return ReferenceExtractor.Extract(1, "terraform", content, symbols);
    }

    private static void AssertReference(IReadOnlyCollection<ReferenceRecord> references, string symbolName)
        => Assert.Contains(references, r => r.SymbolName == symbolName && r.ReferenceKind == "reference");
}

public class PowerShellReferenceExtractorTests
{
    [Fact]
    public void Extract_PowerShell_StatementStartCall_IsReferenced()
    {
        var references = Extract("""
            Write-Host "hello"
            """);

        AssertCall(references, "Write-Host");
    }

    [Fact]
    public void Extract_PowerShell_PipelineCall_IsReferenced()
    {
        var references = Extract("""
            $items | ForEach-Object { Process-One $_ }
            """);

        AssertCall(references, "ForEach-Object");
        AssertCall(references, "Process-One");
    }

    [Fact]
    public void Extract_PowerShell_AssignmentCall_IsReferenced()
    {
        var references = Extract("""
            $result = Invoke-RestMethod -Uri $Uri
            """);

        AssertCall(references, "Invoke-RestMethod");
    }

    [Fact]
    public void Extract_PowerShell_ChainedPipelineCalls_AreReferenced()
    {
        var references = Extract("""
            $items | Where-Object { $_.Enabled } | Select-Object Name
            """);

        AssertCall(references, "Where-Object");
        AssertCall(references, "Select-Object");
    }

    [Fact]
    public void Extract_PowerShell_OperatorsAndComments_DoNotEmitCalls()
    {
        var references = Extract("""
            # Write-Host "ignored"
            if ($count -lt 10) { return }
            $name = "Fake-Call"
            """);

        Assert.DoesNotContain(references, r => r.SymbolName == "Write-Host");
        Assert.DoesNotContain(references, r => r.SymbolName == "lt");
        Assert.DoesNotContain(references, r => r.SymbolName == "Fake-Call");
    }

    private static IReadOnlyList<ReferenceRecord> Extract(string content)
    {
        var symbols = SymbolExtractor.Extract(1, "powershell", content);
        return ReferenceExtractor.Extract(1, "powershell", content, symbols);
    }

    private static void AssertCall(IReadOnlyCollection<ReferenceRecord> references, string symbolName)
        => Assert.Contains(references, r => r.SymbolName == symbolName && r.ReferenceKind == "call");
}

public class BatchReferenceExtractorTests
{
    [Fact]
    public void Extract_Batch_GotoTarget_IsReferenced()
    {
        var references = Extract("""
            goto :Build
            :Build
            """);

        AssertCall(references, "Build");
    }

    [Fact]
    public void Extract_Batch_CallTarget_IsReferenced()
    {
        var references = Extract("""
            call :RunTests
            :RunTests
            """);

        AssertCall(references, "RunTests");
    }

    [Fact]
    public void Extract_Batch_InlineCommandTargets_AreReferenced()
    {
        var references = Extract("""
            goto :Build & call :Package
            :Build
            :Package
            """);

        AssertCall(references, "Build");
        AssertCall(references, "Package");
    }

    [Fact]
    public void Extract_Batch_ConditionalJumpTarget_IsReferenced()
    {
        var references = Extract("""
            if errorlevel 1 goto :Retry
            :Retry
            """);

        AssertCall(references, "Retry");
    }

    [Fact]
    public void Extract_Batch_CommentsEscapesAndEof_DoNotEmitCalls()
    {
        var references = Extract("""
            rem goto :Ignored
            :: call :Commented
            echo ^& goto :Escaped
            goto :EOF
            """);

        Assert.DoesNotContain(references, r => r.SymbolName == "Ignored");
        Assert.DoesNotContain(references, r => r.SymbolName == "Commented");
        Assert.DoesNotContain(references, r => r.SymbolName == "Escaped");
        Assert.DoesNotContain(references, r => r.SymbolName == "EOF");
    }

    private static IReadOnlyList<ReferenceRecord> Extract(string content)
    {
        var symbols = SymbolExtractor.Extract(1, "batch", content);
        return ReferenceExtractor.Extract(1, "batch", content, symbols);
    }

    private static void AssertCall(IReadOnlyCollection<ReferenceRecord> references, string symbolName)
        => Assert.Contains(references, r => r.SymbolName == symbolName && r.ReferenceKind == "call");
}
