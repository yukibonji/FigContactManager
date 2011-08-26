﻿module FigContactManager.Web.Tests

open Formlets
open MbUnit.Framework
open FigContactManager
open FigContactManager.Data
open FigContactManager.Data.Tests
open FigContactManager.Web
open WingBeats
open WingBeats.Xml

[<Test>]
let ``show all groups`` () =
    let cmgr = App.InitializeDatabase connectionString
    let html = showAllGroups cmgr |> Renderer.RenderToString
    printfn "%s" html
    ()

[<Test>]
let ``contact formlet with empty phone and email gives error``() =
    let env = EnvDict.fromStrings [losSerializer.Serialize 1L; "John"; ""; ""]
    printfn "%A" env
    let r = run (contactFormlet Contact.Dummy) env
    match r with
    | Success c -> failwithf "formlet should not have succeeded: %A" c
    | Failure (_,errors) -> 
        Assert.AreEqual(1, errors.Length)
        Assert.AreEqual("Enter either a phone or an email", errors.[0])
    ()