﻿// Copyright Matthias Koch 2018.
// Distributed under the MIT License.
// https://github.com/nuke-build/web/blob/master/LICENSE

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using FluentFTP;
using Nuke.Common.Tools.DocFx;
using Nuke.Common;
using Nuke.Common.Tooling;
using Nuke.Common.Utilities.Collections;
using static CustomTocWriter;
using static Disclaimer;
using static CustomDocFx;
using static NugetPackageLoader;
using static Nuke.Common.IO.SerializationTasks;
using static Nuke.Common.Tools.DocFx.DocFxTasks;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.EnvironmentInfo;
using static Nuke.Common.IO.PathConstruction;
using static Nuke.Common.Logger;


partial class Build : NukeBuild
{
    public static int Main() => Execute<Build>(x => x.BuildSite);

    [Parameter] readonly string FtpUsername;
    [Parameter] readonly string FtpPassword;
    [Parameter] readonly string FtpServer;

    string DocFxFile => RootDirectory / "docfx.json";
    string SiteDirectory => OutputDirectory / "site";

    AbsolutePath GenerationDirectory => TemporaryDirectory / "packages";
    AbsolutePath ApiDirectory => SourceDirectory / "api";

    IEnumerable<ApiProject> Projects => YamlDeserializeFromFile<List<ApiProject>>(RootDirectory / "projects.yml");

    Target Clean => _ => _
        .Executes(() =>
        {
            DeleteDirectory(ApiDirectory);
            DeleteDirectory(GenerationDirectory);
            EnsureCleanDirectory(OutputDirectory);
        });

    Target DownloadPackages => _ => _
        .DependsOn(Clean)
        .Executes(() =>
        {
            InstallPackages(Projects.Select(x => x.PackageId), GenerationDirectory);
        });

    Target CustomDocFx => _ => _
        .DependsOn(DownloadPackages)
        .Executes(() =>
        {
            WriteCustomDotFx(DocFxFile, BuildProjectDirectory / "docfx.template.json", GenerationDirectory, ApiDirectory);
        });

    Target CustomToc => _ => _
        .DependsOn(DownloadPackages, Metadata)
        .Executes(() =>
        {
            GlobFiles(ApiDirectory, "**/toc.yml").ForEach(File.Delete);
            WriteCustomTocs(ApiDirectory, GlobFiles(GenerationDirectory, "**/lib/*/*.dll"));
        });

    Target Disclaimer => _ => _
        .DependsOn(DownloadPackages)
        .Executes(() =>
        {
            Projects.Where(x => x.IsExternalRepository)
                .ForEachLazy(x => Info($"Writing disclaimer for {x.PackageId}..."))
                .ForEach(x => WriteDisclaimer(x,
                    ApiDirectory / $"{x.PackageId}.disclaimer.md",
                    GlobFiles(GenerationDirectory / x.PackageId, "lib/*/*.dll")));
        });

    Target Metadata => _ => _
        .DependsOn(DownloadPackages, CustomDocFx)
        .Executes(() =>
        {
            if (IsLocalBuild)
            {
                //SetVariable ("MSBuildSDKsPath", @"C:\Program Files\dotnet\sdk\2.0.0\Sdks");
                SetVariable("VSINSTALLDIR", @"C:\Program Files (x86)\Microsoft Visual Studio\2017\Professional");
                SetVariable("VisualStudioVersion", "15.0");
            }

            DocFxMetadata(DocFxFile, s => s.SetLogLevel(DocFxLogLevel.Verbose));
        });

    IEnumerable<string> XRefMapFiles
        => GlobFiles(NuGetPackageResolver.GetLocalInstalledPackageDirectory("msdn.4.5.2"), "content/*.zip")
            .Concat(GlobFiles(GenerationDirectory, "specs/xrefmap.yml"));

    Target BuildSite => _ => _
        .DependsOn(Metadata, CustomToc, Disclaimer)
        .Executes(() =>
        {
            DocFxBuild(DocFxFile, s => s
                .SetLogLevel(DocFxLogLevel.Warning)
                .SetXRefMaps(XRefMapFiles)
                .SetServe(IsLocalBuild));
        });

    Target Publish => _ => _
        .DependsOn(BuildSite)
        .Requires(() => FtpUsername, () => FtpPassword, () => FtpServer)
        .Executes(() =>
        {
            var client = new FtpClient(FtpServer, new NetworkCredential(FtpUsername, FtpPassword));
            client.Connect();

            Directory.GetDirectories(SiteDirectory, "*", SearchOption.AllDirectories)
                .ForEach(directory =>
                {
                    var files = GlobFiles(directory, "*").ToArray();
                    var relativePath = GetRelativePath(SiteDirectory, directory);
                    var uploadedFiles = client.UploadFiles(files, relativePath, verifyOptions: FtpVerify.Retry);
                    ControlFlow.Assert(uploadedFiles == files.Length, "uploadedFiles == files.Length");
                });
        });
}
