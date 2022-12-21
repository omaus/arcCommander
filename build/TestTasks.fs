module TestTasks

open Fake.Core
open Fake.DotNet
open BlackFox.Fake

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
