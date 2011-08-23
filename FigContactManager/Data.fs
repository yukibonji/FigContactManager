﻿namespace FigContactManager

open System
open System.Collections.Generic
open System.Data
open Microsoft.FSharp.Reflection

module Data =
    type Contact = {
        Id: int64
        Name: string
        Phone: string
        Email: string
    }
    with static member New name phone email =
            { Id = 0L; Name = name; Phone = phone; Email = email }

    type Group = {
        Id: int64
        Name: string
    }
    with static member New name =
            { Id = 0L; Name = name }

    type ContactGroup = {
        Id: int64
        Group: int64
        Contact: int64
    }
    with static member New group contact =
            { Id = 0L; Group = group; Contact = contact }

    let createConnection (connectionString: string) =
        let conn = new System.Data.SQLite.SQLiteConnection(connectionString)
        conn.Open()
        conn :> IDbConnection


    let private strEq (a: string) (b: string) = 
        StringComparer.InvariantCultureIgnoreCase.Equals(a, b)

    let private keywords = 
        let k = ["order"; "group"]
        HashSet<_>(k, StringComparer.InvariantCultureIgnoreCase)
    let private escape s =
        if keywords.Contains s
            then sprintf "\"%s\"" s // sqlite-specific quote
            else s

    let createSchema conn types =
        let exec a = Sql.execNonQuery (Sql.withConnection conn) a [] |> ignore
        let sqlType t =
            match t with
            | x when x = typeof<int> -> "int"
            | x when x = typeof<int64> -> "int"
            | x when x = typeof<bool> -> "int"
            | x when x = typeof<string> -> "varchar"
            | x when x = typeof<DateTime> -> "datetime"
            | x -> failwithf "Don't know how to express type %A in database" x
        let createTable (escape: string -> string) (sqlType: Type -> string) (t: Type) =
            let fields = 
                FSharpType.GetRecordFields t 
                |> Seq.filter (fun p -> strEq p.Name "id" |> not) // convention: PK is named "id"
            let table = escape t.Name // convention: type name = table name
            let drop = sprintf "drop table if exists %s" table
            let fields = 
                let fieldType (t: Type) =
                    let nullable = t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<option<_>>
                    let sqlType = 
                        let t = 
                            if nullable
                                then t.GetGenericArguments().[0]
                                else t
                        sqlType t
                    let nullable = if nullable then "" else "not"
                    sprintf "%s %s null" sqlType nullable
                fields |> Seq.map (fun f -> sprintf "%s %s" (escape f.Name) (fieldType f.PropertyType))
                // convention: record field name = table column name
            let fields = String.Join(",", Seq.toArray fields)
            let create = sprintf "create table %s (id integer primary key autoincrement, %s)" table fields
            //printfn "%s" create
            exec drop
            exec create
            ()
        types |> Seq.iter (createTable escape sqlType)


    let private P = Sql.Parameter.make

    let inline private (>>=) f x = Tx.bind x f
    let inline private (>>.) x f = Tx.bind (fun _ -> x) f

    let private selectLastId = "select last_insert_rowid();"

    let generateInsert a =
        let allfields = a.GetType() |> Sql.recordFields
        // convention: first field is ID
        let names = allfields |> Seq.skip 1 |> Seq.map (sprintf "@%s") |> Seq.toList
        let sql = sprintf "insert into %s (%s) values (null, %s); %s" 
                    (escape <| a.GetType().Name) // convention: type name = table name
                    (allfields |> Seq.map escape |> String.concat ",")
                    (String.concat "," names) 
                    selectLastId
        let values = Sql.recordValues a |> Seq.skip 1
        let parameters = Seq.zip names values |> Sql.parameters
        sql,parameters

    let generateDeleteId t value = 
        let name = "@i"
        let sql = sprintf "delete from %s where id = %s" // convention: PK is called 'id'
                    (escape <| t)
                    name
        sql,[P(name,value)]

    let generateDelete a =
        // convention: first field is ID
        let value = a |> Sql.recordValues |> Seq.head
        generateDeleteId (a.GetType().Name) value

    let generateUpdate a =
        // convention: first field is ID
        let idValue = a |> Sql.recordValues |> Seq.head
        let idField = "@id"
        let allFieldsButId = a.GetType() |> Sql.recordFields |> Seq.skip 1 
        let fieldsAndParams = 
            allFieldsButId
            |> Seq.map (fun f -> sprintf "%s=@%s" (escape f) f)
            |> Seq.toList
            |> String.concat ","
        let sql = sprintf "update %s set %s where id = %s" // convention: PK is called 'id'
                    (escape <| a.GetType().Name) 
                    fieldsAndParams 
                    idField
        let values = a |> Sql.recordValues |> Seq.skip 1
        let names = allFieldsButId |> Seq.map (sprintf "@%s")
        let parameters = Seq.zip names values |> Sql.parameters |> Seq.toList
        let parameters = P(idField, idValue)::parameters
        sql,parameters

    let genericInsert c =
        generateInsert c 
        ||> Tx.execScalar
        |> Tx.map Option.get

    let generateFindAll (t: Type)  =
        let sql = sprintf "select * from %s" (escape t.Name)
        fun limitOffset ->
            let limitOffset = limitOffset |> Option.fold (fun _ (l,o) -> sprintf " limit %d offset %d" l o) ""
            sql + limitOffset

    type Contact with
        static member Insert (c: Contact) =
            genericInsert c
            |> Tx.map (fun newId -> { c with Id = newId })
        static member Delete (c: Contact) =
            generateDelete c
            ||> Tx.execNonQueryi
        static member DeleteById i =
            generateDeleteId (typeof<Contact>.GetType().Name) i
            ||> Tx.execNonQueryi
        static member FindAll(?limitOffset) = 
            let sql = generateFindAll typeof<Contact> limitOffset
            Tx.execReader sql [] |> Tx.map (Sql.map (Sql.asRecord<Contact> ""))

    type ContactGroup with
        static member Insert (c: ContactGroup) =
            genericInsert c
            |> Tx.map (fun newId -> { c with Id = newId })
        static member Delete (c: ContactGroup) =
            generateDelete c
            ||> Tx.execNonQueryi
        static member DeleteByGroup (c: Group) =
            Tx.execNonQueryi "delete from ContactGroup where \"group\" = @g" [P("@g",c.Id)]

    type Group with
        static member Insert (c: Group) =
            genericInsert c
            |> Tx.map (fun newId -> { c with Id = newId })
        static member private Delete (c: Group) =
            generateDelete c
            ||> Tx.execNonQueryi
        static member DeleteCascade (c: Group) =
            ContactGroup.DeleteByGroup c >>. Group.Delete c
        static member FindAll(?limitOffset) = 
            let sql = generateFindAll typeof<Group> limitOffset
            Tx.execReader sql [] |> Tx.map (Sql.map (Sql.asRecord<Group> ""))

    let connectionString = System.Configuration.ConfigurationManager.ConnectionStrings.["sqlite"].ConnectionString
    let connMgr = Sql.withNewConnection (fun () -> createConnection connectionString)
    