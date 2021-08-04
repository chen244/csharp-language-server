module CSharpLanguageServer.Server

open System
open System.IO
open System.Collections.Generic
open System.Collections.Immutable

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.MSBuild
open Microsoft.CodeAnalysis.FindSymbols
open Microsoft.CodeAnalysis.Text
open Microsoft.CodeAnalysis.Completion
open Microsoft.CodeAnalysis.Rename
open FSharp.Control.Tasks.V2

open LanguageServerProtocol.Server
open LanguageServerProtocol
open LanguageServerProtocol.Types
open LanguageServerProtocol.LspResult

open Helpers

type CSharpLspClient(sendServerNotification: ClientNotificationSender, sendServerRequest: ClientRequestSender) =
    inherit LspClient ()

    override __.WindowShowMessage(p) =
        sendServerNotification "window/showMessage" (box p) |> Async.Ignore

    override __.WindowShowMessageRequest(p) =
        sendServerRequest.Send "window/showMessageRequest" (box p)

    override __.WindowLogMessage(p) =
        sendServerNotification "window/logMessage" (box p) |> Async.Ignore

    override __.TelemetryEvent(p) =
        sendServerNotification "telemetry/event" (box p) |> Async.Ignore

    override __.ClientRegisterCapability(p) =
        sendServerRequest.Send "client/registerCapability" (box p)

    override __.ClientUnregisterCapability(p) =
        sendServerRequest.Send "client/unregisterCapability" (box p)

    override __.WorkspaceWorkspaceFolders () =
        sendServerRequest.Send "workspace/workspaceFolders" ()

    override __.WorkspaceConfiguration (p) =
        sendServerRequest.Send "workspace/configuration" (box p)

    override __.WorkspaceApplyEdit (p) =
        sendServerRequest.Send "workspace/applyEdit" (box p)

    override __.WorkspaceSemanticTokensRefresh () =
      sendServerNotification "workspace/semanticTokens/refresh" () |> Async.Ignore

    override __.TextDocumentPublishDiagnostics(p) =
        sendServerNotification "textDocument/publishDiagnostics" (box p) |> Async.Ignore

type CSharpLspServer(lspClient: CSharpLspClient) =
    inherit LspServer()

    let docs = DocumentStore()

    let mutable clientCapabilities: ClientCapabilities option = None
    let mutable workspace: Workspace option = None

    let logMessage message = lspClient.WindowShowMessage { Type = MessageType.Log ;
                                                           Message = "cs-lsp-server: " + message } |> ignore

    let firstSolutionOnDir dir =
        let files = Directory.GetFiles(dir, "*.sln", SearchOption.AllDirectories) |> Seq.toList

        match files with
        | [x] -> Some x
        | _ -> None

    let mutable deferredInitialize = async {
        let cwd = Directory.GetCurrentDirectory()
        logMessage ("in deferredInitialize, determining solutions on project root: " + cwd)

        match firstSolutionOnDir(cwd) with
        | None -> raise (Exception("no or multiple .sln files found on " + cwd))
        | Some solutionPath ->
            try
                logMessage ("in deferredInitialize, loading solution: " + solutionPath)

                let msbuildWorkspace = MSBuildWorkspace.Create()
                msbuildWorkspace.LoadMetadataForReferencedProjects <- true
                let! _ = msbuildWorkspace.OpenSolutionAsync(solutionPath) |> Async.AwaitTask

                logMessage "in deferredInitialize, ok solution loaded"

                for diag in msbuildWorkspace.Diagnostics do
                    logMessage ("msbuildWorkspace.Diagnostics: " + diag.ToString())

                workspace <- Some(msbuildWorkspace :> Workspace)
                ()
            with
            | ex ->
                logMessage ("deferredInitialize failed with " + ex.ToString())
                return ()
    }

    let currentSolution () = match workspace with
                             | Some ws -> Some ws.CurrentSolution
                             | _ -> None

    let getPathUri path = Uri("file://" + path)

    let getDocumentForUri u =
        let uri = Uri u
        match currentSolution () with
        | Some solution -> let documents = solution.Projects |> Seq.collect (fun p -> p.Documents)
                           let matchingDocuments = documents |> Seq.filter (fun d -> uri = getPathUri d.FilePath) |> List.ofSeq
                           match matchingDocuments with
                           | [d] -> //logMessage ("resolved to document " + d.FilePath + " while looking for " + uri.ToString())
                                    Some d
                           | _ -> None
        | None -> None

    let getSymbolAtPosition documentUri pos = async {
        match getDocumentForUri documentUri with
        | Some doc ->
            let! sourceText = doc.GetTextAsync() |> Async.AwaitTask
            let! semanticModel = doc.GetSemanticModelAsync() |> Async.AwaitTask
            let position = sourceText.Lines.GetPosition(LinePosition(pos.Line, pos.Character))
            let! symbolRef = SymbolFinder.FindSymbolAtPositionAsync(semanticModel, position, workspace.Value) |> Async.AwaitTask
            return if isNull symbolRef then None else Some (symbolRef, doc)
        | None ->
            return None
    }

    let todo() = raise (Exception "TODO")

    let makeRangeForLinePosSpan (pos: LinePositionSpan) =
        { Start = { Line = pos.Start.Line ; Character = pos.Start.Character }
          End = { Line = pos.End.Line ; Character = pos.End.Character } }

    let mapDiagSeverity s =
        match s with
        | Microsoft.CodeAnalysis.DiagnosticSeverity.Info -> Some LanguageServerProtocol.Types.DiagnosticSeverity.Information
        | Microsoft.CodeAnalysis.DiagnosticSeverity.Warning -> Some LanguageServerProtocol.Types.DiagnosticSeverity.Warning
        | Microsoft.CodeAnalysis.DiagnosticSeverity.Error -> Some LanguageServerProtocol.Types.DiagnosticSeverity.Error
        | Microsoft.CodeAnalysis.DiagnosticSeverity.Hidden -> None
        | _ -> None

    let makeLspDiag (d: Microsoft.CodeAnalysis.Diagnostic) =
        { Range = makeRangeForLinePosSpan(d.Location.GetLineSpan().Span)
          Severity = mapDiagSeverity d.Severity
          Code = None
          CodeDescription = None
          Source = "lsp"
          Message = d.GetMessage()
          RelatedInformation = None
          Tags = None
          Data = None }

    let locationToLspLocation (loc: Microsoft.CodeAnalysis.Location) =
        { Uri = loc.SourceTree.FilePath |> getPathUri |> string
          Range = loc.GetLineSpan().Span |> makeRangeForLinePosSpan }

    override _.Initialize(p: InitializeParams) = async {
        clientCapabilities <- p.Capabilities

        return
            { InitializeResult.Default with
                Capabilities =
                    { ServerCapabilities.Default with
                        HoverProvider = Some true
                        RenameProvider = Some true
                        DefinitionProvider = Some true
                        TypeDefinitionProvider = Some false
                        ImplementationProvider = Some false
                        ReferencesProvider = Some true
                        DocumentHighlightProvider = Some true
                        DocumentSymbolProvider = Some false
                        WorkspaceSymbolProvider = Some false
                        DocumentFormattingProvider = Some false
                        DocumentRangeFormattingProvider = Some false
                        SignatureHelpProvider = None
                        CompletionProvider =
                            Some {
                                ResolveProvider = None
                                TriggerCharacters = Some ([| '.'; '''; |])
                                AllCommitCharacters = None //TODO: what chars shoudl commit completions?
                            }
                        CodeLensProvider = None
                        CodeActionProvider = None
                        TextDocumentSync =
                            Some { TextDocumentSyncOptions.Default with
                                     OpenClose = Some false
                                     Save = Some { IncludeText = Some true }
                                     Change = Some TextDocumentSyncKind.Full
                                 }
                        FoldingRangeProvider = Some false
                        SelectionRangeProvider = Some false
                        SemanticTokensProvider = None
                    }
            }
        |> success
    }

    override __.Initialized(_: InitializedParams) = async {
        do! deferredInitialize
        return ()
    }

    override __.Exit(): Async<unit> = async { return () }

    override __.Shutdown(): Async<unit> = async { return () }

    override __.TextDocumentDidOpen(openParams: Types.DidOpenTextDocumentParams) = async {
        let document = getDocumentForUri openParams.TextDocument.Uri

        docs.Open(openParams)

        match document with
        | Some doc ->
            let! semanticModel = doc.GetSemanticModelAsync() |> Async.AwaitTask

            let diagnostics = semanticModel.GetDiagnostics()
                              |> Seq.map makeLspDiag
                              |> Array.ofSeq

            do! lspClient.TextDocumentPublishDiagnostics {
                Uri = openParams.TextDocument.Uri ;
                Diagnostics = diagnostics }

            return ()
        | None -> ()
    }

    override __.TextDocumentDidChange(change: Types.DidChangeTextDocumentParams): Async<unit> = async {
        docs.Change(change)

        match getDocumentForUri change.TextDocument.Uri with
        | Some doc ->
            let fullText = SourceText.From(change.ContentChanges.[0].Text)

            let updatedDoc = doc.WithText(fullText)

            //let updatedSolution = updatedDoc.Project.Solution;
            //let applySucceeded = workspace.Value.TryApplyChanges(updatedSolution)

            //if not applySucceeded then
            //  logMessage "workspace.TryApplyChanges has failed!"

            let! semanticModel = updatedDoc.GetSemanticModelAsync() |> Async.AwaitTask

            let diagnostics = semanticModel.GetDiagnostics()
                                |> Seq.map makeLspDiag
                                |> Array.ofSeq

            do! lspClient.TextDocumentPublishDiagnostics {
                  Uri = change.TextDocument.Uri
                  Diagnostics = diagnostics }
            ()

        | _ -> ()

        return ()
    }


    override __.TextDocumentDidSave(saveParams: Types.DidSaveTextDocumentParams): Async<unit> = async {
        match getDocumentForUri saveParams.TextDocument.Uri with
        | Some doc ->
            let newDoc =
                match saveParams.Text with
                | Some text ->
                    let fullText = SourceText.From(text)
                    let updatedDoc = doc.WithText(fullText)
                    let updatedSolution = updatedDoc.Project.Solution;
                    workspace.Value.TryApplyChanges(updatedSolution) |> ignore
                    updatedDoc
                | None -> doc

            let! semanticModel = newDoc.GetSemanticModelAsync() |> Async.AwaitTask

            let diagnostics = semanticModel.GetDiagnostics()
                            |> Seq.map makeLspDiag
                            |> Array.ofSeq

            do! lspClient.TextDocumentPublishDiagnostics {
                Uri = saveParams.TextDocument.Uri ;
                Diagnostics = diagnostics }
            ()

        | _ -> ()

        return ()
    }

    override __.TextDocumentDidClose(closeParams: Types.DidCloseTextDocumentParams): Async<unit> = async {
        docs.Close(closeParams)
        return ()
    }

    override __.CodeLensResolve(arg1: Types.CodeLens): AsyncLspResult<Types.CodeLens> =
        failwith "Not Implemented"
    override __.CompletionItemResolve(arg1: Types.CompletionItem): AsyncLspResult<Types.CompletionItem> =
        failwith "Not Implemented"
    override __.DocumentLinkResolve(arg1: Types.DocumentLink): AsyncLspResult<Types.DocumentLink> =
        failwith "Not Implemented"
    override __.TextDocumentCodeAction(arg1: Types.CodeActionParams): AsyncLspResult<Types.TextDocumentCodeActionResult option> =
        failwith "Not Implemented"
    override __.TextDocumentCodeLens(arg1: Types.CodeLensParams): AsyncLspResult<Types.CodeLens [] option> =
        failwith "Not Implemented"
    override __.TextDocumentColorPresentation(arg1: Types.ColorPresentationParams): AsyncLspResult<Types.ColorPresentation []> =
        failwith "Not Implemented"

    override __.TextDocumentCompletion(posParams: Types.CompletionParams): AsyncLspResult<Types.CompletionList option> = async {
        match getDocumentForUri posParams.TextDocument.Uri with
        | Some doc ->
            logMessage (sprintf "TextDocumentCompletion, posParams.TextDocument.Uri=%s" (posParams.TextDocument.Uri |> string))

            let maybeStoredText = docs.GetTextByFullName(posParams.TextDocument.Uri)

            let storedDoc = match maybeStoredText with
                            | Some storedText -> doc.WithText(SourceText.From(storedText))
                            | None -> doc

            let! storedDocText = storedDoc.GetTextAsync() |> Async.AwaitTask

            let posInText = storedDocText.Lines.GetPosition(LinePosition(posParams.Position.Line, posParams.Position.Character))

            let completionService = CompletionService.GetService(storedDoc)
            if isNull completionService then
                return ()

            let! maybeCompletionResults =
                    completionService.GetCompletionsAsync(storedDoc, posInText) |> Async.AwaitTask

            match Option.ofObj maybeCompletionResults with
            | Some completionResults ->
                let makeLspCompletionItem (item: Microsoft.CodeAnalysis.Completion.CompletionItem) =
                    { CompletionItem.Create(item.DisplayText) with
                        Kind = item.Tags |> Seq.head |> roslynTagToLspCompletion |> Some ;
                        InsertTextFormat = Some Types.InsertTextFormat.PlainText }

                let completionList = {
                    IsIncomplete = false ;
                    Items = completionResults.Items
                                              |> Seq.map makeLspCompletionItem
                                              |> Array.ofSeq }

                return success (Some completionList)
            | None -> return success None
        | None -> return success None
    }

    override __.TextDocumentDefinition(def: Types.TextDocumentPositionParams): AsyncLspResult<Types.GotoResult option> = async {
        let resolveDefinition (doc: Document) = task {
            let! sourceText = doc.GetTextAsync()
            let position = sourceText.Lines.GetPosition(LinePosition(def.Position.Line, def.Position.Character))

            let! semanticModel = doc.GetSemanticModelAsync()
            let! symbol = SymbolFinder.FindSymbolAtPositionAsync(semanticModel, position, workspace.Value)

            return match symbol with
                    | null -> //logMessage "no symbol at this point"
                        []
                    | sym -> //logMessage ("have symbol " + sym.ToString() + " at this point!")
                        // TODO:
                        //  - handle symbols in metadata

                        let locationsInSource = sym.Locations |> Seq.filter (fun l -> l.IsInSource) |> List.ofSeq

                        match locationsInSource with
                        | [] -> []
                        | locations -> locations |> Seq.map locationToLspLocation |> List.ofSeq
        }

        match getDocumentForUri def.TextDocument.Uri with
        | Some doc -> let! locations = resolveDefinition doc |> Async.AwaitTask
                      return locations |> Array.ofSeq
                                       |> GotoResult.Multiple
                                       |> Some
                                       |> success
        | None     -> return None |> success
    }

    override __.TextDocumentDocumentColor(arg1: Types.DocumentColorParams): AsyncLspResult<Types.ColorInformation []> =
        failwith "Not Implemented"

    override __.TextDocumentDocumentHighlight(docParams: Types.TextDocumentPositionParams): AsyncLspResult<Types.DocumentHighlight [] option> = async {
        match currentSolution () with
        | Some solution ->
            let! maybeSymbol = getSymbolAtPosition docParams.TextDocument.Uri docParams.Position

            match maybeSymbol with
            | Some (symbol, doc) ->
                let docSet = ImmutableHashSet<Document>.Empty.Add(doc)
                let! refs = SymbolFinder.FindReferencesAsync(symbol, solution, docSet) |> Async.AwaitTask
                let locationsFromRefs = refs |> Seq.collect (fun r -> r.Locations) |> Seq.map (fun rl -> rl.Location)

                let! defRef = SymbolFinder.FindSourceDefinitionAsync(symbol, solution) |> Async.AwaitTask
                let locationsFromDef = match Option.ofObj defRef with
                                       // TODO: we might need to skip locations that are on a different document than this one
                                       | Some sym -> sym.Locations |> List.ofSeq
                                       | None -> []

                return (Seq.append locationsFromRefs locationsFromDef)
                       |> Seq.map (fun l -> { Range = (locationToLspLocation l).Range ;
                                              Kind = Some DocumentHighlightKind.Read })
                       |> Array.ofSeq
                       |> Some
                       |> success

            | None -> return None |> success

        | None -> return None |> success
    }

    override __.TextDocumentDocumentLink(arg1: Types.DocumentLinkParams): AsyncLspResult<Types.DocumentLink [] option> =
        failwith "Not Implemented"

    override __.TextDocumentDocumentSymbol(arg1: Types.DocumentSymbolParams): AsyncLspResult<Types.SymbolInformation [] option> =
        failwith "Not Implemented"

    override __.TextDocumentFoldingRange(arg1: Types.FoldingRangeParams): AsyncLspResult<Types.FoldingRange list option> =
        failwith "Not Implemented"

    override __.TextDocumentFormatting(arg1: Types.DocumentFormattingParams): AsyncLspResult<Types.TextEdit [] option> =
        failwith "Not Implemented"

    override __.TextDocumentHover(hoverPos: Types.TextDocumentPositionParams): AsyncLspResult<Types.Hover option> = async {
        let! maybeSymbol = getSymbolAtPosition hoverPos.TextDocument.Uri hoverPos.Position
        let maybeHoverText = Option.map (fun (sym: ISymbol, _: Document) -> sym.ToString() + "\n" +  sym.GetDocumentationCommentXml()) maybeSymbol

        let contents = [ match maybeHoverText with
                         | None -> MarkedString.WithLanguage { Language = "fsharp"; Value = "" }
                         | Some text -> MarkedString.WithLanguage { Language = "fsharp"; Value = text }
                       ]

        return Some { Contents = contents |> Array.ofList |> MarkedStrings
                      Range = None }
               |> success
    }

    override __.TextDocumentImplementation(arg1: Types.TextDocumentPositionParams): AsyncLspResult<Types.GotoResult option> =
        failwith "Not Implemented"
    override __.TextDocumentOnTypeFormatting(arg1: Types.DocumentOnTypeFormattingParams): AsyncLspResult<Types.TextEdit [] option> =
        failwith "Not Implemented"
    override __.TextDocumentRangeFormatting(arg1: Types.DocumentRangeFormattingParams): AsyncLspResult<Types.TextEdit [] option> =
        failwith "Not Implemented"

    override __.TextDocumentReferences(refParams: Types.ReferenceParams): AsyncLspResult<Types.Location [] option> = async {
        match currentSolution () with
        | Some solution ->
            let! maybeSymbol = getSymbolAtPosition refParams.TextDocument.Uri refParams.Position

            match maybeSymbol with
            | Some (symbol, _) ->
                let! refs = SymbolFinder.FindReferencesAsync(symbol, solution) |> Async.AwaitTask
                return refs |> Seq.collect (fun r -> r.Locations)
                            |> Seq.map (fun rl -> locationToLspLocation rl.Location)
                            |> Array.ofSeq
                            |> Some
                            |> success
            | None -> return None |> success

        | None -> return None |> success
    }

    override __.TextDocumentRename(rename: Types.RenameParams): AsyncLspResult<Types.WorkspaceEdit option> = async {
        let renameSymbolInDoc symbol (doc: Document) = async {
            let originalSolution = doc.Project.Solution
            let! updatedSolution = Renamer.RenameSymbolAsync(doc.Project.Solution,
                                                             symbol,
                                                             rename.NewName,
                                                             doc.Project.Solution.Workspace.Options)
                                   |> Async.AwaitTask

            // make a list of changes
            let changedDocs = updatedSolution.GetChanges(originalSolution)
                                                .GetProjectChanges()
                                |> Seq.collect (fun pc -> pc.GetChangedDocuments())

            let docTextEdits = List<TextDocumentEdit>()

            for docId in changedDocs do
                let originalDoc = originalSolution.GetDocument(docId)
                let! originalDocText = originalDoc.GetTextAsync() |> Async.AwaitTask

                let updatedDoc = updatedSolution.GetDocument(docId)
                let! docChanges = updatedDoc.GetTextChangesAsync(originalDoc) |> Async.AwaitTask

                let diffEdits: TextEdit array =
                    docChanges |> Seq.sortBy (fun c -> c.Span.Start)
                               |> Seq.map (fun c -> { Range = originalDocText.Lines.GetLinePositionSpan(c.Span)
                                                              |> makeRangeForLinePosSpan
                                                      NewText = c.NewText })
                               |> Array.ofSeq

                docTextEdits.Add(
                    { TextDocument = { Uri = originalDoc.FilePath |> getPathUri |> string
                                       Version = docs.GetVersionByFullName(originalDoc.FilePath) }
                      Edits = diffEdits })

            return docTextEdits |> List.ofSeq
        }

        let! maybeSymbol = getSymbolAtPosition rename.TextDocument.Uri rename.Position

        let! docChanges = match maybeSymbol with
                            | Some (symbol, doc) -> renameSymbolInDoc symbol doc
                            | None -> async { return [] }

        return WorkspaceEdit.Create (docChanges |> Array.ofList, clientCapabilities.Value) |> Some |> success
    }

    override __.TextDocumentSelectionRange(arg1: Types.SelectionRangeParams): AsyncLspResult<Types.SelectionRange list option> =
        failwith "Not Implemented"
    override __.TextDocumentSemanticTokensFull(arg1: Types.SemanticTokensParams): AsyncLspResult<Types.SemanticTokens option> =
        failwith "Not Implemented"
    override __.TextDocumentSemanticTokensFullDelta(arg1: Types.SemanticTokensDeltaParams): AsyncLspResult<U2<Types.SemanticTokens,Types.SemanticTokensDelta> option> =
        failwith "Not Implemented"
    override __.TextDocumentSemanticTokensRange(arg1: Types.SemanticTokensRangeParams): AsyncLspResult<Types.SemanticTokens option> =
        failwith "Not Implemented"
    override __.TextDocumentSignatureHelp(arg1: Types.SignatureHelpParams): AsyncLspResult<Types.SignatureHelp option> =
        failwith "Not Implemented"
    override __.TextDocumentTypeDefinition(def: Types.TextDocumentPositionParams): AsyncLspResult<Types.GotoResult option> =
        failwith "Not Implemented"
    override __.TextDocumentWillSave(arg1: Types.WillSaveTextDocumentParams): Async<unit> =
        failwith "Not Implemented"
    override __.TextDocumentWillSaveWaitUntil(arg1: Types.WillSaveTextDocumentParams): AsyncLspResult<Types.TextEdit [] option> =
        failwith "Not Implemented"
    override __.WorkspaceDidChangeConfiguration(arg1: Types.DidChangeConfigurationParams): Async<unit> =
        failwith "Not Implemented"
    override __.WorkspaceDidChangeWatchedFiles(arg1: Types.DidChangeWatchedFilesParams): Async<unit> =
        failwith "Not Implemented"
    override __.WorkspaceDidChangeWorkspaceFolders(arg1: Types.DidChangeWorkspaceFoldersParams): Async<unit> =
        failwith "Not Implemented"
    override __.WorkspaceExecuteCommand(arg1: Types.ExecuteCommandParams): AsyncLspResult<Newtonsoft.Json.Linq.JToken> =
        failwith "Not Implemented"
    override __.WorkspaceSymbol(arg1: Types.WorkspaceSymbolParams): AsyncLspResult<Types.SymbolInformation [] option> =
        failwith "Not Implemented"

    override __.Dispose(): unit = ()

let startCore () =
    use input = Console.OpenStandardInput()
    use output = Console.OpenStandardOutput()

    let requestsHandlings = defaultRequestHandlings<CSharpLspServer> ()
    LanguageServerProtocol.Server.start requestsHandlings
                                        input
                                        output
                                        CSharpLspClient
                                        (fun lspClient -> new CSharpLspServer(lspClient))

let start () =
    try
        let result = startCore ()
        int result
    with
    | ex ->
        // logger.error (Log.setMessage "Start - LSP mode crashed" >> Log.addExn ex)
        3