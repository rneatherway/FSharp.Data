﻿namespace FSharp.Data.Runtime

open System
open System.ComponentModel
open System.Globalization
open System.IO
open System.Text
open System.Text.RegularExpressions
open System.Xml
open FSharp.Data
open FSharp.Data.Runtime

#nowarn "10001"

// --------------------------------------------------------------------------------------

type HtmlTableCell = 
    | Cell of bool * string
    | Empty
    member x.IsHeader =
        match x with
        | Empty -> true
        | Cell(h, _) -> h
    member x.Data = 
        match x with
        | Empty -> ""
        | Cell(_, d) -> d

type HtmlTable = 
    { Name : string
      HeaderNamesAndUnits : (string * Type option)[]
      Rows :  string [] []
      Html : HtmlNode }
    member x.Headers = Array.map fst x.HeaderNamesAndUnits
    override x.ToString() =
        let sb = StringBuilder()
        use wr = new StringWriter(sb) 
        wr.WriteLine(x.Name)
        let headers = 
            x.HeaderNamesAndUnits 
            |> Array.map (fun (header, unit) ->
                match unit with
                | None -> header
                | Some unit -> header + " (" + unit.Name + ")")
        let data = array2D <| List.ofArray headers :: (x.Rows |> Array.map (List.ofArray) |> List.ofArray)
        let rows = data.GetLength(0)
        let columns = data.GetLength(1)
        let widths = Array.zeroCreate columns 
        data |> Array2D.iteri (fun _ c cell ->
            widths.[c] <- max (widths.[c]) (cell.Length))
        for r in 0 .. rows - 1 do
            for c in 0 .. columns - 1 do
                wr.Write(data.[r,c].PadRight(widths.[c] + 1))
            wr.WriteLine()
        sb.ToString()

// --------------------------------------------------------------------------------------

/// Helper functions called from the generated code for working with HTML tables
module HtmlRuntime =

    let private getTableName defaultName nameSet (element:HtmlNode) (parents:HtmlNode list) = 

        let tryGetName choices =
            choices
            |> List.tryPick (fun (attrName) -> 
                match element.TryGetAttribute attrName with
                | Some(HtmlAttribute(_,value)) -> Some <| value
                | None -> None
            )

        let rec tryFindPrevious f (x:HtmlNode) (parents:HtmlNode list)= 
            match parents with
            | p::rest ->
                let nearest = 
                    HtmlNode.descendants true (fun _ -> true) p 
                    |> Seq.takeWhile ((<>) x) 
                    |> Seq.filter f
                    |> Seq.toList |> List.rev
                match nearest with
                | [] -> tryFindPrevious f p rest
                | h :: _ -> Some h 
            | [] -> None

        let deriveFromSibling element parents = 
            let isHeading s =
                let name = HtmlNode.name s
                Regex.IsMatch(name, """h\d""")
            tryFindPrevious isHeading element parents            

        match deriveFromSibling element parents with
        | Some(e) when not(Set.contains e.InnerText nameSet) -> e.InnerText
        | _ ->
                match element.Descendants ["caption"] with
                | [] ->
                     match tryGetName [ "id"; "name"; "title"; "summary"] with
                     | Some(name) when not(Set.contains name nameSet) -> name
                     | _ -> defaultName
                | h :: _ -> h.InnerText
                
    let private parseTable includeLayoutTables (index, nameSet) (table:HtmlNode) (parents:HtmlNode list) missingValues cultureInfo unitsOfMeasureProvider = 

        let rows = table.Descendants(["tr"], true, false) |> List.mapi (fun i r -> i,r)
        
        if rows.Length <= 1 then None else

        let cells = rows |> List.map (fun (_,r) -> r.Elements ["td"; "th"] |> List.mapi (fun i e -> i, e))
        let rowLengths = cells |> List.map (fun x -> x.Length)
        let numberOfColumns = List.max rowLengths
        
        if not includeLayoutTables && (rowLengths |> List.filter (fun x -> x > 1) |> List.length <= 1) then None else

        let res = Array.init rows.Length  (fun _ -> Array.init numberOfColumns (fun _ -> Empty))
        for rowindex, _ in rows do
            for colindex, cell in cells.[rowindex] do
                let rowSpan = max 1 (cell.GetAttributeValue(0, Int32.TryParse, "rowspan")) - 1
                let colSpan = max 1 (cell.GetAttributeValue(0, Int32.TryParse, "colspan")) - 1

                let data =
                    let getContents contents = 
                        (contents |> List.map (HtmlNode.innerTextExcluding ["table"; "ul"; "ol"; "sup"; "sub"]) |> String.Concat).Replace(Environment.NewLine, "").Trim()
                    match cell with
                    | HtmlElement("td", _, contents) -> Cell (false, getContents contents)
                    | HtmlElement("th", _, contents) -> Cell (true, getContents contents)
                    | _ -> Empty
                let col_i = ref colindex
                while !col_i < res.[rowindex].Length && res.[rowindex].[!col_i] <> Empty do incr(col_i)
                for j in [!col_i..(!col_i + colSpan)] do
                    for i in [rowindex..(rowindex + rowSpan)] do
                        if i < rows.Length && j < numberOfColumns
                        then res.[i].[j] <- data

        let startIndex, headers = 
            if res.[0] |> Array.forall (fun r -> r.IsHeader) 
            then 1, res.[0] |> Array.map (fun x -> x.Data) |> Some
            else res |> Array.map (Array.map (fun x -> x.Data)) |> HtmlInference.inferHeaders missingValues cultureInfo
            
        let headerNamesAndUnits, _ = CsvInference.parseHeaders headers numberOfColumns "" unitsOfMeasureProvider

        let tableName = getTableName (sprintf "Table%d" (index + 1)) nameSet table parents
        let rows = res.[startIndex..] |> Array.map (Array.map (fun x -> x.Data))

        Some { Name = tableName
               HeaderNamesAndUnits = headerNamesAndUnits
               Rows = rows 
               Html = table }

    let getTables includeLayoutTables missingValues cultureInfo unitsOfMeasureProvider (doc:HtmlDocument) =
        let tableElements = 
            [ doc.Elements ["table"]
              |> List.map (fun table -> table, [])
              
              doc.Elements(fun x -> x.Elements ["table"] <> [])
              |> List.collect (fun parent -> parent.Elements ["table"] |> List.map (fun table -> table, [parent]))
              
              doc.Descendants(fun x -> x.Elements(fun x -> x.Elements ["table"] <> []) <> [])
              |> List.collect (fun grandParent -> grandParent.Elements() |> List.collect (fun parent -> parent.Elements ["table"] |> List.map (fun table -> table, [parent; grandParent])))
            ]
            |> List.concat
            |> (fun x -> if includeLayoutTables 
                         then x 
                         else x |> List.filter (fun (e,_) -> (e.HasAttribute("cellspacing", "0") && e.HasAttribute("cellpadding", "0")) |> not)
                )
        let (_,_,tables) =
            tableElements
            |> List.fold (fun (index, names, tables) (table, parents) -> 
                            match parseTable includeLayoutTables (index, names) table parents missingValues cultureInfo unitsOfMeasureProvider with
                            | Some(table) -> index + 1, Set.add table.Name names, table::tables
                            | None -> index + 1, names, tables
                         ) (0, Set.empty, [])
        tables |> List.rev

type TypedHtmlDocument internal (doc:HtmlDocument, tables:Map<string,HtmlTable>) =

    member x.Html = doc

    /// [omit]
    [<EditorBrowsableAttribute(EditorBrowsableState.Never)>]
    [<CompilerMessageAttribute("This method is intended for use in generated code only.", 10001, IsHidden=true, IsError=false)>]
    static member Create(includeLayoutTables, missingValuesStr, cultureStr, reader:TextReader) =
        let missingValues = TextRuntime.GetMissingValues missingValuesStr
        let cultureInfo = TextRuntime.GetCulture cultureStr
        let doc = 
            reader 
            |> HtmlDocument.Load
        let tables = 
            doc
            |> HtmlRuntime.getTables includeLayoutTables missingValues cultureInfo None
            |> List.map (fun table -> table.Name, table) 
            |> Map.ofList
        TypedHtmlDocument(doc, tables)

    /// [omit]
    [<EditorBrowsableAttribute(EditorBrowsableState.Never)>]
    [<CompilerMessageAttribute("This method is intended for use in generated code only.", 10001, IsHidden=true, IsError=false)>]
    member __.GetTable(id:string) = 
       tables |> Map.find id

and HtmlTable<'rowType> internal (name:string, headers, values:'rowType[], html:HtmlNode) =

    member x.Name = name
    member x.Headers = headers
    member x.Rows = values

    member x.Html = html

    /// [omit]
    [<EditorBrowsableAttribute(EditorBrowsableState.Never)>]
    [<CompilerMessageAttribute("This method is intended for use in generated code only.", 10001, IsHidden=true, IsError=false)>]
    static member Create(rowConverter:Func<string[],'rowType>, doc:TypedHtmlDocument, id:string) =
       let table = doc.GetTable id
       HtmlTable<_>(table.Name, table.Headers, Array.map rowConverter.Invoke table.Rows, table.Html)
