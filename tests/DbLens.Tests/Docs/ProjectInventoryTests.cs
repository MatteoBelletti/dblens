using DbLens.Docs.Inventory;
using FluentAssertions;
using static DbLens.Tests.Docs.InventoryTestSupport;

namespace DbLens.Tests.Docs;

public sealed class ProjectInventoryTests
{
    [Fact]
    public void AProject_ListsItsSdkOutputFrameworkReferencesAndPackages()
    {
        var inventory = Build(
            File("Directory.Packages.props", "<Project><ItemGroup><PackageVersion Include=\"Serilog\" Version=\"4.0.0\" /></ItemGroup></Project>"),
            File("src/Api/Api.csproj", """
                <Project Sdk="Microsoft.NET.Sdk.Web">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="..\Core\Core.csproj" />
                    <PackageReference Include="Serilog" />
                    <PackageReference Include="Npgsql" Version="9.0.1" />
                  </ItemGroup>
                </Project>
                """),
            Project("src/Core/Core.csproj"));

        var api = inventory.Item("project:Api");
        api.Attributes.Should().Contain("sdk", "Microsoft.NET.Sdk.Web")
            .And.Contain("outputType", "Exe").And.Contain("outputTypeFrom", "sdk")
            .And.Contain("targetFrameworks", "net10.0")
            .And.Contain("projectReferences", "Core")
            .And.Contain("packages", "Npgsql@9.0.1, Serilog@4.0.0");
        inventory.Item("project:Core").Attributes.Should().Contain("outputType", "Library").And.Contain("outputTypeFrom", "default");
        inventory.Coverage.Should().BeEmpty();
    }

    [Fact]
    public void TheNearestPropsFiles_DecideInheritedValues()
    {
        var inventory = Build(
            File("Directory.Packages.props", "<Project><ItemGroup><PackageVersion Include=\"X\" Version=\"1.0.0\" /></ItemGroup></Project>"),
            File("src/Directory.Packages.props", "<Project><ItemGroup><PackageVersion Include=\"X\" Version=\"2.0.0\" /></ItemGroup></Project>"),
            File("Directory.Build.props", "<Project><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>"),
            File("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"X\" /></ItemGroup></Project>"));

        inventory.Item("project:App").Attributes.Should().Contain("packages", "X@2.0.0")
            .And.Contain("targetFrameworks", "net9.0").And.Contain("targetFrameworksFrom", "Directory.Build.props");
    }

    [Fact]
    public void WhatMsbuildWouldResolve_IsListedWithoutAValue_AndCounted()
    {
        var inventory = Build(
            File("src/App/App.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <PackageReference Include="A" Version="$(AVersion)" />
                    <PackageReference Include="B" />
                    <ProjectReference Include="..\Missing\Missing.csproj" />
                    <ProjectReference Include="$(Shared)\Shared.csproj" />
                  </ItemGroup>
                </Project>
                """));

        inventory.Item("project:App").Attributes.Should().Contain("packages", "A, B").And.Contain("targetFrameworks", "unknown");
        inventory.Coverage.Select(c => (c.Count, c.Note.Split(' ')[0])).Should().BeEquivalentTo([(2, "Package"), (2, "Project"), (1, "Projects")]);
    }

    [Fact]
    public void AProjectFileThatIsNotXml_IsSkipped_AndSaysSo()
    {
        var inventory = Build(File("src/Bad/Bad.csproj", "<Project"), Project("src/Ok/Ok.csproj"));

        inventory.Of(ItemKind.Project).Should().ContainSingle().Which.Id.Should().Be("project:Ok");
        inventory.Coverage.Should().Contain(c => c.Note.Contains("not valid XML") && c.Count == 1);
    }

    [Fact]
    public void NoProjectAtAll_IsNotAnAllClear()
    {
        var inventory = Build(File("src/readme.cs", "class A {}"));

        inventory.Items.Should().BeEmpty();
        inventory.Coverage.Should().Contain(c => c.Note == "No .csproj file was found under the root.");
    }
}
