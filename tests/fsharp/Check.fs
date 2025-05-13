namespace common.tests

open FsCheck
open FsCheck.FSharp

type TestConfig =
    { MaxTests: int option
      MaxDegreesOfParallelism: int option }

    static member Default =
        { MaxTests = None
          MaxDegreesOfParallelism = None }

[<RequireQualifiedAccess>]
module Check =
    let private fromGenWithConfig config gen f =
        let arb = Arb.fromGen gen
        let property = Prop.forAll arb (f >> ignore)
        Check.One(config, property)

    let fromGen gen testConfig f =
        let mutable config = Config.QuickThrowOnFailure

        testConfig.MaxTests
        |> Option.iter (fun maxTests -> config <- config.WithMaxTest maxTests)

        testConfig.MaxDegreesOfParallelism
        |> Option.iter (fun maxDegreesOfParallelism ->
            config <-
                config.WithParallelRunConfig({ ParallelRunConfig.MaxDegreeOfParallelism = maxDegreesOfParallelism }))

        fromGenWithConfig config gen f
