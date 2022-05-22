/// LaunchDarkly configuration (feature flags)
module LibService.LaunchDarkly

open FSharp.Control.Tasks
open System.Threading.Tasks

open Prelude
open Tablecloth

open LaunchDarkly.Sdk
open LaunchDarkly.Sdk.Server

module Internal =

  // For testing
  // See https://docs.launchdarkly.com/sdk/features/test-data-sources#net-server-side
  let testData = Integrations.TestData.DataSource()

  let client =
    lazy
      (match Config.launchDarklyApiKey with
       | Some key ->
         let config =
           Configuration
             .Builder(key)
             .StartWaitTime(System.TimeSpan.FromSeconds(1))
             .DiagnosticOptOut(false)
             .Offline(false)
             .Logging(Components.NoLogging)
             .Build()
         new LdClient(config)
       | None ->
         let config =
           Configuration
             .Builder("test")
             .DataSource(testData)
             .StartWaitTime(System.TimeSpan.FromSeconds(0))
             .DiagnosticOptOut(true)
             .Offline(false)
             .Logging(Components.NoLogging)
             .Build()
         new LdClient(config))

  // Internal functions to call LD. Should only be used from within
  let boolSetTestDefault (flagName : string) (default_ : bool) =
    testData.Update(testData.Flag(flagName).ValueForAllUsers(LdValue.Of default_))
    |> ignore<Integrations.TestData>

  let intSetTestDefault (flagName : string) (default_ : int) =
    testData.Update(testData.Flag(flagName).ValueForAllUsers(LdValue.Of default_))
    |> ignore<Integrations.TestData>

  let floatSetTestDefault (flagName : string) (default_ : float) =
    testData.Update(testData.Flag(flagName).ValueForAllUsers(LdValue.Of default_))
    |> ignore<Integrations.TestData>

  let stringSetTestDefault (flagName : string) (default_ : string) =
    testData.Update(testData.Flag(flagName).ValueForAllUsers(LdValue.Of default_))
    |> ignore<Integrations.TestData>

  let boolVar (flagName : string) (userSignifier : string) (default_ : bool) : bool =
    client.Force().BoolVariation(flagName, User.WithKey userSignifier, default_)

  let intVar (flagName : string) (userSignifier : string) (default_ : int) : int =
    client.Force().IntVariation(flagName, User.WithKey userSignifier, default_)

  let floatVar
    (flagName : string)
    (userSignifier : string)
    (default_ : float)
    : float =
    // Note use of name Double here (C# double is an F# float)
    client.Force().DoubleVariation(flagName, User.WithKey userSignifier, default_)

  let stringVar
    (flagName : string)
    (userSignifier : string)
    (default_ : string)
    : string =
    client.Force().StringVariation(flagName, User.WithKey userSignifier, default_)


  // -------------
  // System values
  // Functions to create dynamic configuration, which can be set remotely. These
  // provide knobs to change how the system works remotely, for example, changing the
  // number of queue events to execute simulateously.
  // -------------
  let boolConfig
    (name : string)
    (default_ : bool)
    (testDefault : bool)
    : unit -> bool =
    boolSetTestDefault name testDefault
    fun () -> boolVar name "system" default_

  let intConfig (name : string) (default_ : int) (testDefault : int) : unit -> int =
    intSetTestDefault name testDefault
    fun () -> intVar name "system" default_

  let floatConfig
    (name : string)
    (default_ : float)
    (testDefault : float)
    : unit -> float =
    floatSetTestDefault name testDefault
    fun () -> floatVar name "system" default_

  let stringConfig
    (name : string)
    (default_ : string)
    (testDefault : string)
    : unit -> string =
    stringSetTestDefault name testDefault
    fun () -> stringVar name "system" default_



  // -------------
  // per-handler values
  // -------------

  let flagNameForHandler (canvasName : CanvasName.T) (tlid : tlid) =
    // Use tlid instead of a desc because a tlid is canonical. A desc would be better
    // because it's user readable but it's not too hard to find a tlid id
    $"handler-{canvasName}-{tlid}"

  let handlerBool
    (name : string)
    (default_ : bool)
    (testDefault : bool)
    : CanvasName.T -> tlid -> bool =
    boolSetTestDefault name testDefault
    fun canvasName tlid -> boolVar name (flagNameForHandler canvasName tlid) default_

  let handlerInt
    (name : string)
    (default_ : int)
    (testDefault : int)
    : CanvasName.T -> tlid -> int =
    intSetTestDefault name testDefault
    fun canvasName tlid -> intVar name (flagNameForHandler canvasName tlid) default_

  let handlerFloat
    (name : string)
    (default_ : float)
    (testDefault : float)
    : CanvasName.T -> tlid -> float =
    floatSetTestDefault name testDefault
    fun canvasName tlid ->
      floatVar name (flagNameForHandler canvasName tlid) default_

  let handlerString
    (name : string)
    (default_ : string)
    (testDefault : string)
    : CanvasName.T -> tlid -> string =
    stringSetTestDefault name testDefault
    fun canvasName tlid ->
      stringVar name (flagNameForHandler canvasName tlid) default_


  // -------------
  // per-canvas values
  // -------------

  let canvasBool
    (name : string)
    (default_ : bool)
    (testDefault : bool)
    : CanvasName.T -> bool =
    boolSetTestDefault name testDefault
    fun canvasName -> boolVar name $"canvas-{canvasName}" default_

  let canvasInt
    (name : string)
    (default_ : int)
    (testDefault : int)
    : CanvasName.T -> int =
    intSetTestDefault name testDefault
    fun canvasName -> intVar name $"canvas-{canvasName}" default_

  let canvasFloat
    (name : string)
    (default_ : float)
    (testDefault : float)
    : CanvasName.T -> float =
    floatSetTestDefault name testDefault
    fun canvasName -> floatVar name $"canvas-{canvasName}" default_

  let canvasString
    (name : string)
    (default_ : string)
    (testDefault : string)
    : CanvasName.T -> string =
    stringSetTestDefault name testDefault
    fun canvasName -> stringVar name $"canvas-{canvasName}" default_



let flush () : unit = Internal.client.Force().Dispose()

// --------------
// Handler Flags - per-canvas, per-handler settings
// --------------

let traceSamplingRule =
  Internal.handlerString "traces-sampling-rule" "sample-one-in-10" "sample-all"

// --------------
// Canvas Flags - these are per-canvas settings
// --------------

let useEventsV2 = Internal.canvasBool "use-events-v2" false false

// --------------
// System flags - this allows us to change the run-time values of system
// configuration. This is the preferred way of setting arbitrary numbers
// --------------

let queueAllowedExecutionTimeInSeconds =
  Internal.intConfig "queue-allowed-execution-time-in-seconds" 300 300

/// Limit to the number of events each QueueWorker will run concurrently
let queueMaxConcurrentEventsPerWorker =
  // 4 is conservating, we probably set this much higher
  Internal.intConfig "queue-max-concurrent-events-per-worker" 4 4

/// Delay between fetches from the queue when something goes wrong
let queueDelayBetweenPullsInMillis =
  Internal.intConfig "queue-delay-between-pubsub-pulls-in-millis" 1000 1000
