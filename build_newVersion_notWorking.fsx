#r "paket:
nuget BlackFox.Fake.BuildTask
nuget Fake.Core.Target
nuget Fake.Core.Process
nuget Fake.Core.ReleaseNotes
nuget Fake.Core.UserInput
nuget Fake.IO.FileSystem
nuget Fake.DotNet.Cli
nuget Fake.DotNet.MSBuild
nuget Fake.DotNet.AssemblyInfoFile
nuget Fake.DotNet.Paket
nuget Fake.DotNet.FSFormatting
nuget Fake.DotNet.Fsi
nuget Fake.DotNet.NuGet
nuget Fake.Api.Github
nuget Fake.DotNet.Testing.Expecto 
nuget Fake.Extensions.Release 0.2.0
nuget Fake.IO.Zip
nuget Fake.Tools.Git //"

#if !FAKE
#load "./.fake/build.fsx/intellisense.fsx"
#r "netstandard" // Temp fix for https://github.com/dotnet/fsharp/issues/5216
#endif

open BlackFox.Fake
open System.IO
open Fake.Core
open Fake.DotNet
open Fake.IO
open Fake.IO.FileSystemOperators
open Fake.IO.Globbing.Operators
open Fake.Tools

[<AutoOpen>]
/// user interaction prompts for critical build tasks where you may want to interrupt when you see wrong inputs.
module MessagePrompts =

    let prompt (msg:string) =
        System.Console.Write(msg)
        System.Console.ReadLine().Trim()
        |> function | "" -> None | s -> Some s
        |> Option.map (fun s -> s.Replace ("\"","\\\""))

    let rec promptYesNo msg =
        match prompt (sprintf "%s [Yn]: " msg) with
        | Some "Y" | Some "y" -> true
        | Some "N" | Some "n" -> false
        | _ -> System.Console.WriteLine("Sorry, invalid answer"); promptYesNo msg

    let releaseMsg = """This will stage all uncommitted changes, push them to the origin and bump the release version to the latest number in the RELEASE_NOTES.md file. 
        Do you want to continue?"""

    let releaseDocsMsg = """This will push the docs to gh-pages. Remember building the docs prior to this. Do you want to continue?"""

/// Executes a dotnet command in the given working directory
let runDotNet cmd workingDir =
    let result =
        DotNet.exec (DotNet.Options.withWorkingDirectory workingDir) cmd ""
    if result.ExitCode <> 0 then failwithf "'dotnet %s' failed in %s" cmd workingDir

/// Metadata about the project
module ProjectInfo = 

    let project = "ArcCommander"

    let testProject = "tests/ArcCommander.Tests.NetCore/ArcCommander.Tests.NetCore.fsproj"

    let summary = "ArcCommander is a command line tool to create, manage and share your ARCs."

    let solutionFile  = "ArcCommander.sln"

    let configuration = "Release"

    // Git configuration (used for publishing documentation in gh-pages branch)
    // The profile where the project is posted
    let gitOwner = "nfdi4plants"
    let gitHome = sprintf "%s/%s" "https://github.com" gitOwner

    let gitName = "arcCommander"

    let website = "/arcCommander"

    let pkgDir = "pkg"

    let publishDir = "publish"

    let release = ReleaseNotes.load "RELEASE_NOTES.md"

    let projectRepo = "https://github.com/nfdi4plants/arcCommander"

    let stableVersion = SemVer.parse release.NugetVersion

    let stableVersionTag = (sprintf "%i.%i.%i" stableVersion.Major stableVersion.Minor stableVersion.Patch )

    let mutable prereleaseSuffix = ""

    let mutable prereleaseTag = ""

    let mutable isPrerelease = false


/// Barebones, minimal build tasks
module BasicTasks = 

    open ProjectInfo

    let setPrereleaseTag = BuildTask.create "SetPrereleaseTag" [] {
        printfn "Please enter pre-release package suffix"
        let suffix = System.Console.ReadLine()
        prereleaseSuffix <- suffix
        prereleaseTag <- (sprintf "%s-%s" release.NugetVersion suffix)
        isPrerelease <- true
    }

    let clean = BuildTask.create "Clean" [] {
        !! "src/**/bin"
        ++ "src/**/obj"
        ++ "pkg"
        ++ "bin"
        |> Shell.cleanDirs 
    }

    let cleanTestResults = 
        BuildTask.create "cleanTestResults" [] {
            Shell.cleanDirs (!! "tests/**/**/TestResult")
        }
    
    let build = BuildTask.create "Build" [clean] {
        solutionFile
        |> DotNet.build id
    }

    let copyBinaries = BuildTask.create "CopyBinaries" [clean; build] {
        let targets = 
            !! "src/**/*.??proj"
            -- "src/**/*.shproj"
            |>  Seq.map (fun f -> ((Path.getDirectory f) </> "bin" </> configuration, "bin" </> (Path.GetFileNameWithoutExtension f)))
        for i in targets do printfn "%A" i
        targets
        |>  Seq.iter (fun (fromDir, toDir) -> Shell.copyDir toDir fromDir (fun _ -> true))
    }

/// Test executing build tasks
module TestTasks = 

    open ProjectInfo
    open BasicTasks

    let runTests = BuildTask.create "RunTests" [clean; cleanTestResults; build; copyBinaries] {
        let standardParams = Fake.DotNet.MSBuild.CliArguments.Create ()
        Fake.DotNet.DotNet.test(fun testParams ->
            {
                testParams with
                    Logger = Some "console;verbosity=detailed"
                    Configuration = DotNet.BuildConfiguration.fromString configuration
                    NoBuild = true
            }
        ) testProject
    }

/// Package creation
module PackageTasks = 

    open ProjectInfo

    open BasicTasks
    open TestTasks

    let pack = BuildTask.create "Pack" [clean; build; runTests; copyBinaries] {
        if promptYesNo (sprintf "creating stable package with version %s OK?" stableVersionTag ) 
            then
                !! "src/**/*.*proj"
                |> Seq.iter (Fake.DotNet.DotNet.pack (fun p ->
                    let msBuildParams =
                        {p.MSBuildParams with 
                            Properties = ([
                                "Version",stableVersionTag
                                "PackageReleaseNotes",  (release.Notes |> String.concat "\r\n")
                            ] @ p.MSBuildParams.Properties)
                        }
                    {
                        p with 
                            MSBuildParams = msBuildParams
                            OutputPath = Some pkgDir
                            NoBuild = true
                            Configuration = DotNet.BuildConfiguration.fromString configuration
                    }
                ))
        else failwith "aborted"
    }

    let packPrerelease = BuildTask.create "PackPrerelease" [setPrereleaseTag; clean; build; runTests; copyBinaries] {
        if promptYesNo (sprintf "package tag will be %s OK?" prereleaseTag )
            then 
                !! "src/**/*.*proj"
                //-- "src/**/Plotly.NET.Interactive.fsproj"
                |> Seq.iter (Fake.DotNet.DotNet.pack (fun p ->
                            let msBuildParams =
                                {p.MSBuildParams with 
                                    Properties = ([
                                        "Version", prereleaseTag
                                        "PackageReleaseNotes",  (release.Notes |> String.toLines )
                                    ] @ p.MSBuildParams.Properties)
                                }
                            {
                                p with 
                                    VersionSuffix = Some prereleaseSuffix
                                    OutputPath = Some pkgDir
                                    NoBuild = true
                                    Configuration = DotNet.BuildConfiguration.fromString configuration
                            }
                ))
        else
            failwith "aborted"
    }

    let publishBinariesWin = BuildTask.create "PublishBinariesWin" [clean.IfNeeded; build.IfNeeded] {
        let outputPath = sprintf "%s/win-x64" publishDir
        solutionFile
        |> DotNet.publish (fun p ->
            let standardParams = Fake.DotNet.MSBuild.CliArguments.Create ()
            {
                p with
                    Runtime = Some "win-x64"
                    Configuration = DotNet.BuildConfiguration.fromString configuration
                    OutputPath = Some outputPath
                    MSBuildParams = {
                        standardParams with
                            Properties = [
                                "Version", stableVersionTag
                                "Platform", "x64"
                                "PublishSingleFile", "true"
                            ]
                    };
            }
        )
        printfn "Beware that assemblyName differs from projectName!"
    }

    let publishBinariesLinux = BuildTask.create "PublishBinariesLinux" [clean.IfNeeded; build.IfNeeded] {
        let outputPath = sprintf "%s/linux-x64" publishDir
        solutionFile
        |> DotNet.publish (fun p ->
            let standardParams = Fake.DotNet.MSBuild.CliArguments.Create ()
            {
                p with
                    Runtime = Some "linux-x64"
                    Configuration = DotNet.BuildConfiguration.fromString configuration
                    OutputPath = Some outputPath
                    MSBuildParams = {
                        standardParams with
                            Properties = [
                                "Version", stableVersionTag
                                "Platform", "x64"
                                "PublishSingleFile", "true"
                            ]
                    }
            }
        )
        printfn "Beware that assemblyName differs from projectName!"
    }

    let publishBinariesMac = BuildTask.create "PublishBinariesMac" [clean.IfNeeded; build.IfNeeded] {
        let outputPath = sprintf "%s/osx-x64" publishDir
        solutionFile
        |> DotNet.publish (fun p ->
            let standardParams = Fake.DotNet.MSBuild.CliArguments.Create ()
            {
                p with
                    Runtime = Some "osx-x64"
                    Configuration = DotNet.BuildConfiguration.fromString configuration
                    OutputPath = Some outputPath
                    MSBuildParams = {
                        standardParams with
                            Properties = [
                                "Version", stableVersionTag
                                "Platform", "x64"
                                "PublishSingleFile", "true"
                            ]
                    }
            }
        )
        printfn "Beware that assemblyName differs from projectName!"
    }

    let publishBinariesAll = BuildTask.createEmpty "PublishBinariesAll" [clean; build; publishBinariesWin; publishBinariesLinux; publishBinariesMac]

module ToolTasks =

    open ProjectInfo
    open BasicTasks
    open TestTasks
    open PackageTasks

    let installPackagedTool = BuildTask.create "InstallPackagedTool" [packPrerelease] {
        Directory.ensure "tests/tool-tests"
        runDotNet "new tool-manifest --force" "tests/tool-tests"
        runDotNet (sprintf "tool install --add-source ../../%s ArcCommander --version %s" pkgDir prereleaseTag) "tests/tool-tests"
    }

    let testPackagedTool = BuildTask.create "TestPackagedTool" [installPackagedTool] {
        runDotNet "ArcCommander --help" "tests/tool-tests"
    }

/// Build tasks for documentation setup and development
module DocumentationTasks =

    open ProjectInfo

    open BasicTasks

    let buildDocs = BuildTask.create "BuildDocs" [build; copyBinaries] {
        printfn "building docs with stable version %s" stableVersionTag
        runDotNet 
            (sprintf "fsdocs build --eval --clean --property Configuration=Release --parameters fsdocs-package-version %s" stableVersionTag)
            "./"
    }

    let buildDocsPrerelease = BuildTask.create "BuildDocsPrerelease" [setPrereleaseTag; build; copyBinaries] {
        printfn "building docs with prerelease version %s" prereleaseTag
        runDotNet 
            (sprintf "fsdocs build --eval --clean --property Configuration=Release --parameters fsdocs-package-version %s" prereleaseTag)
            "./"
    }

    let watchDocs = BuildTask.create "WatchDocs" [build; copyBinaries] {
        printfn "watching docs with stable version %s" stableVersionTag
        runDotNet 
            (sprintf "fsdocs watch --eval --clean --property Configuration=Release --parameters fsdocs-package-version %s" stableVersionTag)
            "./"
    }

    let watchDocsPrerelease = BuildTask.create "WatchDocsPrerelease" [setPrereleaseTag; build; copyBinaries] {
        printfn "watching docs with prerelease version %s" prereleaseTag
        runDotNet 
            (sprintf "fsdocs watch --eval --clean --property Configuration=Release --parameters fsdocs-package-version %s" prereleaseTag)
            "./"
    }

/// Buildtasks that release stuff, e.g. packages, git tags, documentation, etc.
module ReleaseTasks =

    open ProjectInfo

    open BasicTasks
    open TestTasks
    open PackageTasks
    open DocumentationTasks

    let createTag = BuildTask.create "CreateTag" [clean; build; copyBinaries; runTests; pack] {
        if promptYesNo (sprintf "tagging branch with %s OK?" stableVersionTag ) then
            Git.Branches.tag "" stableVersionTag
            Git.Branches.pushTag "" projectRepo stableVersionTag
        else
            failwith "aborted"
    }

    let createPrereleaseTag = BuildTask.create "CreatePrereleaseTag" [setPrereleaseTag; clean; build; copyBinaries; runTests; packPrerelease] {
        if promptYesNo (sprintf "tagging branch with %s OK?" prereleaseTag ) then 
            Git.Branches.tag "" prereleaseTag
            Git.Branches.pushTag "" projectRepo prereleaseTag
        else
            failwith "aborted"
    }

    
    let publishNuget = BuildTask.create "PublishNuget" [clean; build; copyBinaries; runTests; pack] {
        let targets = (!! (sprintf "%s/*.*pkg" pkgDir ))
        for target in targets do printfn "%A" target
        let msg = sprintf "release package with version %s?" stableVersionTag
        if promptYesNo msg then
            let source = "https://api.nuget.org/v3/index.json"
            let apikey =  Environment.environVar "NUGET_KEY"
            for artifact in targets do
                let result = DotNet.exec id "nuget" (sprintf "push -s %s -k %s %s --skip-duplicate" source apikey artifact)
                if not result.OK then failwith "failed to push packages"
        else failwith "aborted"
    }

    let publishNugetPrerelease = BuildTask.create "PublishNugetPrerelease" [clean; build; copyBinaries; runTests; packPrerelease] {
        let targets = (!! (sprintf "%s/*.*pkg" pkgDir ))
        for target in targets do printfn "%A" target
        let msg = sprintf "release package with version %s?" prereleaseTag 
        if promptYesNo msg then
            let source = "https://api.nuget.org/v3/index.json"
            let apikey =  Environment.environVar "NUGET_KEY"
            for artifact in targets do
                let result = DotNet.exec id "nuget" (sprintf "push -s %s -k %s %s --skip-duplicate" source apikey artifact)
                if not result.OK then failwith "failed to push packages"
        else failwith "aborted"
    }

    let releaseDocs =  BuildTask.create "ReleaseDocs" [buildDocs] {
        let msg = sprintf "release docs for version %s?" stableVersionTag
        if promptYesNo msg then
            Shell.cleanDir "temp"
            Git.CommandHelper.runSimpleGitCommand "." (sprintf "clone %s temp/gh-pages --depth 1 -b gh-pages" projectRepo) |> ignore
            Shell.copyRecursive "output" "temp/gh-pages" true |> printfn "%A"
            Git.CommandHelper.runSimpleGitCommand "temp/gh-pages" "add ." |> printfn "%s"
            let cmd = sprintf """commit -a -m "Update generated documentation for version %s""" stableVersionTag
            Git.CommandHelper.runSimpleGitCommand "temp/gh-pages" cmd |> printfn "%s"
            Git.Branches.push "temp/gh-pages"
        else failwith "aborted"
    }

    let prereleaseDocs =  BuildTask.create "PrereleaseDocs" [buildDocsPrerelease] {
        let msg = sprintf "release docs for version %s?" prereleaseTag
        if promptYesNo msg then
            Shell.cleanDir "temp"
            Git.CommandHelper.runSimpleGitCommand "." (sprintf "clone %s temp/gh-pages --depth 1 -b gh-pages" projectRepo) |> ignore
            Shell.copyRecursive "output" "temp/gh-pages" true |> printfn "%A"
            Git.CommandHelper.runSimpleGitCommand "temp/gh-pages" "add ." |> printfn "%s"
            let cmd = sprintf """commit -a -m "Update generated documentation for version %s""" prereleaseTag
            Git.CommandHelper.runSimpleGitCommand "temp/gh-pages" cmd |> printfn "%s"
            Git.Branches.push "temp/gh-pages"
        else failwith "aborted"
    }

module ReleaseNoteTasks =

    open Fake.Extensions.Release

    let updateVersionOfReleaseWorkflow (stableVersionTag) = 
        printfn "Start updating release-github workflow to version %s" stableVersionTag
        let filePath = Path.Combine(__SOURCE_DIRECTORY__,".github/workflows/release-github.yml")
        let s = File.readAsString filePath
        let lastVersion = System.Text.RegularExpressions.Regex.Match(s,@"v\d+.\d+.\d+").Value
        s.Replace(lastVersion,$"v{stableVersionTag}")
        |> File.writeString false filePath

    let createAssemblyVersion = BuildTask.create "createvfs" [] {
        AssemblyVersion.create ProjectInfo.gitName
    }


    let updateReleaseNotes = BuildTask.createFn "ReleaseNotes" [] (fun config ->
        Release.exists()

        Release.update(ProjectInfo.gitOwner, ProjectInfo.gitName, config)

        let release = ReleaseNotes.load "RELEASE_NOTES.md"
    
        Fake.DotNet.AssemblyInfoFile.createFSharp "src/ArcCommander/Server/Version.fs"
            [   Fake.DotNet.AssemblyInfo.Title "ArcCommander"
                Fake.DotNet.AssemblyInfo.Version release.AssemblyVersion
                Fake.DotNet.AssemblyInfo.Metadata (
                    "ReleaseDate", 
                    release.Date |> Option.defaultValue System.DateTime.Today |> fun d -> d.ToShortDateString()
                )
            ]

        let stableVersion = SemVer.parse release.NugetVersion

        let stableVersionTag = (sprintf "%i.%i.%i" stableVersion.Major stableVersion.Minor stableVersion.Patch )

        updateVersionOfReleaseWorkflow (stableVersionTag)
    )

    let githubDraft = BuildTask.createFn "GithubDraft" [] (fun config ->

        let body = "We are ready to go for the first release!"

        Github.draft(
            ProjectInfo.gitOwner,
            ProjectInfo.gitName,
            (Some body),
            None,
            config
        )
    )



module Docker =
    
    open Fake.Core
    open System.Runtime.InteropServices

    //let initializeContext () =
    //    let execContext = Context.FakeExecutionContext.Create false "build.fsx" [ ]
    //    Context.setExecutionContext (Context.RuntimeContext.Fake execContext)

    module Proc =
        module Parallel =
            open System

            let locker = obj()

            let colors =
                [| ConsoleColor.Blue
                   ConsoleColor.Yellow
                   ConsoleColor.Magenta
                   ConsoleColor.Cyan
                   ConsoleColor.DarkBlue
                   ConsoleColor.DarkYellow
                   ConsoleColor.DarkMagenta
                   ConsoleColor.DarkCyan |]

            let print color (colored: string) (line: string) =
                lock locker
                    (fun () ->
                        let currentColor = Console.ForegroundColor
                        Console.ForegroundColor <- color
                        Console.Write colored
                        Console.ForegroundColor <- currentColor
                        Console.WriteLine line)

            let onStdout index name (line: string) =
                let color = colors.[index % colors.Length]
                if isNull line then
                    print color $"{name}: --- END ---" ""
                else if String.isNotNullOrEmpty line then
                    print color $"{name}: " line

            let onStderr name (line: string) =
                let color = ConsoleColor.Red
                if isNull line |> not then
                    print color $"{name}: " line

            let redirect (index, (name, createProcess)) =
                createProcess
                |> CreateProcess.redirectOutputIfNotRedirected
                |> CreateProcess.withOutputEvents (onStdout index name) (onStderr name)

            let printStarting indexed =
                for (index, (name, c: CreateProcess<_>)) in indexed do
                    let color = colors.[index % colors.Length]
                    let wd =
                        c.WorkingDirectory
                        |> Option.defaultValue ""
                    let exe = c.Command.Executable
                    let args = c.Command.Arguments.ToStartInfo
                    print color $"{name}: {wd}> {exe} {args}" ""

            let run cs =
                cs
                |> Seq.toArray
                |> Array.indexed
                |> fun x -> printStarting x; x
                |> Array.map redirect
                |> Array.Parallel.map Proc.run

    let createProcess exe arg dir =
        CreateProcess.fromRawCommandLine exe arg
        |> CreateProcess.withWorkingDirectory dir
        |> CreateProcess.ensureExitCode

    let dotnet = createProcess "dotnet"

    let docker = createProcess "docker"

    //let npm =
    //    let npmPath =
    //        match ProcessUtils.tryFindFileOnPath "npm" with
    //        | Some path -> path
    //        | None ->
    //            "npm was not found in path. Please install it and make sure it's available from your path. " +
    //            "See https://safe-stack.github.io/docs/quickstart/#install-pre-requisites for more info"
    //            |> failwith

    //    createProcess npmPath
    //let npx =
    //    let npxPath =
    //        match ProcessUtils.tryFindFileOnPath "npx" with
    //        | Some path -> path
    //        | None ->
    //            "npx was not found in path. Please install it and make sure it's available from your path."
    //            |> failwith
    //    createProcess npxPath
    //let node =
    //    let nodePath =
    //        match ProcessUtils.tryFindFileOnPath "node" with
    //        | Some path -> path
    //        | None ->
    //            "node was not found in path. Please install it and make sure it's available from your path."
    //            |> failwith
    //    createProcess nodePath

    
    let dockerCompose = createProcess "docker-compose"

    ///Choose process to open plots with depending on OS. Thanks to @zyzhu for hinting at a solution (https://github.com/plotly/Plotly.NET/issues/31)
    let openBrowser url =
        if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then
            CreateProcess.fromRawCommand "cmd.exe" [ "/C"; $"start {url}" ] |> Proc.run |> ignore
        elif RuntimeInformation.IsOSPlatform(OSPlatform.Linux) then
            CreateProcess.fromRawCommand "xdg-open" [ url ] |> Proc.run |> ignore
        elif RuntimeInformation.IsOSPlatform(OSPlatform.OSX) then
            CreateProcess.fromRawCommand "open" [ url ] |> Proc.run |> ignore
        else
            failwith "Cannot open Browser. OS not supported."

    let run proc arg dir =
        proc arg dir
        |> Proc.run
        |> ignore

    let runParallel processes =
        processes
        |> Proc.Parallel.run
        |> ignore

    let runOrDefault args =
        Trace.trace (sprintf "%A" args)
        try
            match args with
            | [| target |] -> Target.runOrDefault target
            | arr when args.Length > 1 ->
                Target.run 0 (Array.head arr) ( Array.tail arr |> List.ofArray )
            | _ -> Target.runOrDefault "Ignore" 
            0
        with e ->
            printfn "%A" e
            1

    open Fake.Extensions.Release

    let dockerImageName = "freymaurer/swate"
    let dockerContainerName = "swate"

    // Change target to github-packages
    // https://docs.github.com/en/actions/publishing-packages/publishing-docker-images
    Target.create "docker-publish" (fun _ ->
        let releaseNotesPath = "RELEASE_NOTES.md"
        let port = "5000"

        Release.exists()
        let newRelease = ReleaseNotes.load releaseNotesPath
        let check = Fake.Core.UserInput.getUserInput($"Is version {newRelease.SemVer.Major}.{newRelease.SemVer.Minor}.{newRelease.SemVer.Patch} correct? (y/n/true/false)" )

        let docker = createProcess "docker"

        let dockerCreateImage() = run docker $"build -t {dockerContainerName} -f build/Dockerfile.publish . " ""
        let dockerTestImage() = run docker $"run -it -p {port}:{port} {dockerContainerName}" ""
        let dockerTagImage() =
            run docker $"tag {dockerContainerName}:latest {dockerImageName}:{newRelease.SemVer.Major}.{newRelease.SemVer.Minor}.{newRelease.SemVer.Patch}" ""
            run docker $"tag {dockerContainerName}:latest {dockerImageName}:latest" ""
        let dockerPushImage() =
            run docker $"push {dockerImageName}:{newRelease.SemVer.Major}.{newRelease.SemVer.Minor}.{newRelease.SemVer.Patch}" ""
            run docker $"push {dockerImageName}:latest" ""
        let dockerPublish() =
            Trace.trace $"Tagging image with :latest and :{newRelease.SemVer.Major}.{newRelease.SemVer.Minor}.{newRelease.SemVer.Patch}"
            dockerTagImage()
            Trace.trace $"Pushing image to dockerhub with :latest and :{newRelease.SemVer.Major}.{newRelease.SemVer.Minor}.{newRelease.SemVer.Patch}"
            dockerPushImage()
        // Check if next SemVer is correct
        match check with
        | "y"|"true"|"Y" ->
            Trace.trace "Perfect! Starting with docker publish"
            Trace.trace "Creating image"
            dockerCreateImage()
            /// Check if user wants to test image
            let testImage = Fake.Core.UserInput.getUserInput($"Want to test the image? (y/n/true/false)" )
            match testImage with
            | "y"|"true"|"Y" ->
                Trace.trace $"Your app on port {port} will open on localhost:{port}."
                dockerTestImage()
                /// Check if user wants the image published
                let imageWorkingCorrectly = Fake.Core.UserInput.getUserInput($"Is the image working as intended? (y/n/true/false)" )
                match imageWorkingCorrectly with
                | "y"|"true"|"Y"    -> dockerPublish()
                | "n"|"false"|"N"   -> Trace.traceErrorfn "Cancel docker-publish"
                | anythingElse      -> failwith $"""Could not match your input "{anythingElse}" to a valid input. Please try again."""
            | "n"|"false"|"N"   -> dockerPublish()
            | anythingElse      -> failwith $"""Could not match your input "{anythingElse}" to a valid input. Please try again."""
        | "n"|"false"|"N" ->
            Trace.traceErrorfn "Please update your SemVer Version in %s" releaseNotesPath
        | anythingElse -> failwith $"""Could not match your input "{anythingElse}" to a valid input. Please try again."""

    )

open BasicTasks
open TestTasks
open PackageTasks
open DocumentationTasks
open ReleaseTasks

/// Full release of nuget package, git tag, and documentation for the stable version.
let _release = 
    BuildTask.createEmpty 
        "Release" 
        [clean; build; copyBinaries; runTests; pack; buildDocs; createTag; publishNuget; releaseDocs]

/// Full release of nuget package, git tag, and documentation for the prerelease version.
let _preRelease = 
    BuildTask.createEmpty 
        "PreRelease" 
        [setPrereleaseTag; clean; build; copyBinaries; runTests; packPrerelease; buildDocsPrerelease; createPrereleaseTag; publishNugetPrerelease; prereleaseDocs]

// run copyBinaries by default
BuildTask.runOrDefaultWithArguments copyBinaries