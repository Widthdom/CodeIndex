using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using CodeIndex.Indexer;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Models;

namespace CodeIndex.Tests;

public partial class ReferenceExtractorTests
{
    [Fact]
    public void Extract_Terraform_DottedReferences_AreReferenced()
    {
        const string content = """
            variable "region" {
              type = string
            }

            variable "instance_count" {
              type = number
            }

            locals {
              common_tags = {
                Environment = "prod"
              }
              name_prefix = "app-${var.region}"
            }

            data "aws_ami" "ubuntu" {
              most_recent = true
            }

            module "network" {
              source = "./modules/network"
              region = var.region
            }

            resource "aws_instance" "web" {
              ami           = data.aws_ami.ubuntu.id
              count         = var.instance_count
              availability_zone = var.region
              subnet_id     = module.network.subnet_id
              tags          = local.common_tags
              depends_on    = [module.network, aws_s3_bucket.foo]
            }

            resource "aws_s3_bucket" "foo" {
              bucket = "example"
            }

            output "instance_ids" {
              value = aws_instance.web[*].id
            }

            resource "aws:s3_bucket" "data" {
              bucket = "example-data"
            }

            output "endpoint" {
              value = module.network.outputs.endpoint
            }

            output "bucket" {
              value = aws:s3_bucket.data.id
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "terraform", content);
        var references = ReferenceExtractor.Extract(1, "terraform", content, symbols);

        Assert.Equal(2, references.Count(reference =>
            reference.SymbolName == "region"
            && reference.ReferenceKind == "reference"));
        Assert.Single(references.Where(reference =>
            reference.SymbolName == "instance_count"
            && reference.ReferenceKind == "reference"));
        Assert.Single(references.Where(reference =>
            reference.SymbolName == "common_tags"
            && reference.ReferenceKind == "reference"));
        Assert.Single(references.Where(reference =>
            reference.SymbolName == "ubuntu"
            && reference.ReferenceKind == "reference"));
        Assert.Equal(3, references.Count(reference =>
            reference.SymbolName == "network"
            && reference.ReferenceKind == "reference"));
        Assert.Single(references.Where(reference =>
            reference.SymbolName == "web"
            && reference.ReferenceKind == "reference"));
        Assert.Single(references.Where(reference =>
            reference.SymbolName == "foo"
            && reference.ReferenceKind == "reference"));
        Assert.Single(references.Where(reference =>
            reference.SymbolName == "endpoint"
            && reference.ReferenceKind == "reference"));
        Assert.Single(references.Where(reference =>
            reference.SymbolName == "data"
            && reference.ReferenceKind == "reference"));
        Assert.DoesNotContain(references, reference =>
            reference.ReferenceKind == "call"
            && reference.SymbolName is "region" or "instance_count" or "common_tags" or "ubuntu" or "network" or "web" or "foo" or "endpoint" or "data");
    }

    [Fact]
    public void Extract_Terraform_VarOrLocal_AsRawObject_IsReferenced_Issue1502()
    {
        const string content = """
            variable "instances" {
              type = map(object({ size = string }))
            }

            variable "max_size" {
              type = number
            }

            locals {
              regions = ["us-east-1", "us-west-2"]
              suffix  = "demo"
            }

            output "ids" {
              value = var.max_size
            }

            resource "aws_instance" "fleet" {
              for_each = var.instances
              count    = length(local.regions)
              tags     = local.suffix
            }
            """;

        var symbols = SymbolExtractor.Extract(1, "terraform", content);
        var references = ReferenceExtractor.Extract(1, "terraform", content, symbols);

        Assert.Single(references.Where(reference =>
            reference.SymbolName == "instances"
            && reference.ReferenceKind == "reference"));
        Assert.Single(references.Where(reference =>
            reference.SymbolName == "max_size"
            && reference.ReferenceKind == "reference"));
        Assert.Single(references.Where(reference =>
            reference.SymbolName == "regions"
            && reference.ReferenceKind == "reference"));
        Assert.Single(references.Where(reference =>
            reference.SymbolName == "suffix"
            && reference.ReferenceKind == "reference"));
    }
}
