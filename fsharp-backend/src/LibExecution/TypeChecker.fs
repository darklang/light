module LibExecution.TypeChecker

open FSharpPlus

open Prelude
open Tablecloth
open Prelude.Tablecloth
open RuntimeTypes

module Error =
  type UnificationError = { expectedType : DType; actualValue : Dval }

  type MismatchedFields =
    { expectedFields : Set<string>
      actualFields : Set<string> }

  type T =
    | TypeLookupFailure of string * int
    | TypeUnificationFailure of UnificationError
    | MismatchedRecordFields of MismatchedFields

  let toString (t : T) : string =
    match t with
    | TypeLookupFailure (lookupName, lookupVersion) ->
        let lookupString = $"({lookupName}, v{lookupVersion})"
        $"Type {lookupString} could not be found on the canvas"
    | TypeUnificationFailure uf ->
        let expected = DvalRepr.typeToDeveloperReprV0 uf.expectedType
        let actual = DvalRepr.prettyTypename uf.actualValue
        $"Expected to see a value of type {expected} but found a {actual}"
    | MismatchedRecordFields mrf ->
        let expected = mrf.expectedFields
        let actual = mrf.actualFields
        // More or less wholesale from User_db's type checker
        let missingFields = Set.difference expected actual in
        let extraFields = Set.difference actual expected in

        let missingMsg =
          "Expected but did not find: ["
          + (missingFields |> Set.toList |> String.concat ", ")
          + "]"

        let extraMsg =
          "Found but did not expect: ["
          + (extraFields |> Set.toList |> String.concat ", ")
          + "]"

        (match (Set.isEmpty missingFields, Set.isEmpty extraFields) with
         | false, false -> $"{missingMsg} & {extraMsg}"
         | false, true -> missingMsg
         | true, false -> extraMsg
         | true, true ->
             "Type checker error! Deduced expected fields from type and actual fields in value did not match, but could not find any examples!")


  let listToString ts = ts |> List.map toString |> String.concat ", "

open Error


type TypeEnv = Map<string * int, UserType.T>

// This converts our list of user_tipes to a (name, version) -> user_tipe lookup
// table. This corresponds to our lookup key in a TUserType of string * int variant
// of a tipe
let userTypeListToTypeEnv (types : List<UserType.T>) : TypeEnv =
  List.fold
    Map.empty
    (fun map (t : UserType.T) ->
      match t.name with
      | "" -> map
      | name -> Map.add (name, t.version) t map)
    types



let rec unify
  (typeEnv : TypeEnv)
  (expected : DType)
  (value : Dval)
  : Result<unit, List<Error.T>> =
  match (expected, value) with
  // Any should be removed, but we currently allow it as a param tipe
  // in user functions, so we should allow it here.
  //
  // Potentially needs to be removed before we use this type checker for DBs?
  //   - Could always have a type checking context that allows/disallows any
  | TVariable _, _ -> Ok()
  | TInt, DInt _ -> Ok()
  | TFloat, DFloat _ -> Ok()
  | TBool, DBool _ -> Ok()
  | TNull, DNull -> Ok()
  | TStr, DStr _ -> Ok()
  | TList _, DList _ -> Ok()
  | TDate, DDate _ -> Ok()
  | TDict _, DObj _ -> Ok()
  | TRecord _, DObj _ -> Ok()
  | TFn _, DFnVal _ -> Ok()
  | TPassword, DPassword _ -> Ok()
  | TUuid, DUuid _ -> Ok()
  | TOption _, DOption _ -> Ok()
  | TResult _, DResult _ -> Ok()
  | TChar, DChar _ -> Ok()
  | TDB _, DDB _ -> Ok()
  | THttpResponse _, DHttpResponse _ -> Ok()
  | TBytes, DBytes _ -> Ok()
  | TUserType (expectedName, expectedVersion), DObj dmap ->
      (match Map.tryFind (expectedName, expectedVersion) typeEnv with
       | None -> Error [ TypeLookupFailure(expectedName, expectedVersion) ]
       | Some ut ->
           (match ut.definition with
            | UserType.UTRecord utd -> unifyUserRecordWithDvalMap typeEnv utd dmap))
  | expectedType, actualValue ->
      Error [ TypeUnificationFailure
                { expectedType = expectedType; actualValue = actualValue } ]


and unifyUserRecordWithDvalMap
  (typeEnv : TypeEnv)
  (definition : List<UserType.RecordField>)
  (value : DvalMap)
  : Result<unit, List<Error.T>> =
  let completeDefinition =
    definition
    |> List.filterMap
         (fun (d : UserType.RecordField) ->
           if d.name = "" then None else Some(d.name, d.typ))
    |> Map.ofList

  let definitionNames = completeDefinition |> Map.keys |> Set.ofList
  let objNames = value |> Map.keys |> Set.ofList
  let sameNames = definitionNames = objNames in

  if sameNames then
    value
    |> Map.toList
    |> List.map
         (fun (key, data) ->
           unify typeEnv (Map.get key completeDefinition |> Option.unwrapUnsafe) data)
    |> Result.combineErrorsUnit
    |> Result.mapError List.concat
  else
    Error [ MismatchedRecordFields
              { expectedFields = definitionNames; actualFields = objNames } ]


let checkFunctionCall
  (userTypes : List<UserType.T>)
  (fn : Fn)
  (args : DvalMap)
  : Result<unit, List<Error.T>> =
  let typeEnv = userTypeListToTypeEnv userTypes in
  let args = Map.toList args in

  let withParams : List<Param * Dval> =
    List.map
      (fun (argname, argval) ->
        let parameter =
          fn.parameters
          |> List.find (fun (p : Param) -> p.name = argname)
          |> Option.unwrapUnsafe

        (parameter, argval))
      args

  withParams
  |> List.map (fun (param, value) -> unify typeEnv param.typ value)
  |> Result.combineErrorsUnit
  |> Result.mapError List.concat


// let check_function_return_type
//     ~(user_tipes : user_tipe list) (fn : fn) (result : dval) :
//     (unit, Error.t list) Result.t =
//   let type_env = user_tipe_list_to_type_env user_tipes in
//   unify ~type_env fn.return_type result
//
