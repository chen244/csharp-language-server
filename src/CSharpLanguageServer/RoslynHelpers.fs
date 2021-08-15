module CSharpLanguageServer.RoslynHelpers

open System
open System.Collections.Generic
open System.IO
open System.Reflection
open System.Threading
open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.CodeActions
open Microsoft.CodeAnalysis.CodeRefactorings
open Microsoft.CodeAnalysis.CSharp
open Microsoft.CodeAnalysis.FindSymbols
open Microsoft.CodeAnalysis.Text
open LanguageServerProtocol
open Microsoft.CodeAnalysis.MSBuild

let roslynTagToLspCompletion tag =
    match tag with
    | "Class"         -> Types.CompletionItemKind.Class
    | "Delegate"      -> Types.CompletionItemKind.Class
    | "Enum"          -> Types.CompletionItemKind.Enum
    | "Interface"     -> Types.CompletionItemKind.Interface
    | "Struct"        -> Types.CompletionItemKind.Class
    | "Local"         -> Types.CompletionItemKind.Variable
    | "Parameter"     -> Types.CompletionItemKind.Variable
    | "RangeVariable" -> Types.CompletionItemKind.Variable
    | "Const"         -> Types.CompletionItemKind.Value
    | "EnumMember"    -> Types.CompletionItemKind.Enum
    | "Event"         -> Types.CompletionItemKind.Function
    | "Field"         -> Types.CompletionItemKind.Field
    | "Method"        -> Types.CompletionItemKind.Method
    | "Property"      -> Types.CompletionItemKind.Property
    | "Label"         -> Types.CompletionItemKind.Unit
    | "Keyword"       -> Types.CompletionItemKind.Keyword
    | "Namespace"     -> Types.CompletionItemKind.Module
    | _ -> Types.CompletionItemKind.Property

let lspPositionForRoslynLinePosition (pos: LinePosition): Types.Position =
    { Line = pos.Line ; Character = pos.Character }

let roslynLinePositionForLspPosition (pos: Types.Position) =
    LinePosition(pos.Line, pos.Character)

let roslynLinePositionSpanForLspRange (range: Types.Range) =
    LinePositionSpan(
        roslynLinePositionForLspPosition range.Start,
        roslynLinePositionForLspPosition range.End)

let lspRangeForRoslynLinePosSpan (pos: LinePositionSpan): Types.Range =
    { Start = lspPositionForRoslynLinePosition pos.Start
      End = lspPositionForRoslynLinePosition pos.End }

let lspTextEditForRoslynTextChange (docText: SourceText) (c: TextChange): Types.TextEdit =
    { Range = docText.Lines.GetLinePositionSpan(c.Span) |> lspRangeForRoslynLinePosSpan
      NewText = c.NewText }

let lspDocChangesFromSolutionDiff
        originalSolution
        (updatedSolution: Solution)
        (docs: DocumentStore): Async<Types.TextDocumentEdit list> = async {

    let getPathUri path = Uri("file://" + path)

    // make a list of changes
    let changedDocs = updatedSolution
                            .GetChanges(originalSolution)
                            .GetProjectChanges()
                            |> Seq.collect (fun pc -> pc.GetChangedDocuments())

    let docTextEdits = List<Types.TextDocumentEdit>()

    for docId in changedDocs do
        let originalDoc = originalSolution.GetDocument(docId)
        let! originalDocText = originalDoc.GetTextAsync() |> Async.AwaitTask
        let updatedDoc = updatedSolution.GetDocument(docId)
        let! docChanges = updatedDoc.GetTextChangesAsync(originalDoc) |> Async.AwaitTask

        let diffEdits: Types.TextEdit array =
            docChanges
            |> Seq.sortBy (fun c -> c.Span.Start)
            |> Seq.map (lspTextEditForRoslynTextChange originalDocText)
            |> Array.ofSeq

        docTextEdits.Add(
            { TextDocument = { Uri = originalDoc.FilePath |> getPathUri |> string
                               Version = docs.GetVersionByFullName(originalDoc.FilePath) }
              Edits = diffEdits })

    return docTextEdits |> List.ofSeq
}

let roslynCodeActionToLspCodeAction originalSolution docs logMessage (ca: CodeActions.CodeAction): Async<Types.CodeAction option> = async {

    let asyncMaybeOnException op = async {
        try
            let! value = op ()
            return Some value
        with ex ->
            logMessage ("roslynCodeActionToLspCodeAction: failed on " + (string ca) + "; ex=" + (string ex))
            return None
    }

    let! maybeOps = asyncMaybeOnException (fun () -> ca.GetOperationsAsync(CancellationToken.None) |> Async.AwaitTask)

    match maybeOps with
    | None -> return None
    | Some ops ->

        let op = ops |> Seq.map (fun o -> o :?> ApplyChangesOperation)
                    |> Seq.head

        let! docTextEdit = lspDocChangesFromSolutionDiff originalSolution
                                                        op.ChangedSolution
                                                        docs

        let edit: Types.WorkspaceEdit = {
            Changes = None
            DocumentChanges = docTextEdit |> Array.ofList |> Some
        }

        return Some {
            Title = ca.Title
            Kind = None
            Diagnostics = None
            Edit = edit
            Command = None
        }
}

type DocumentSymbolCollector(documentUri) =
    inherit CSharpSyntaxWalker(SyntaxWalkerDepth.Token)

    let mutable collectedSymbols: Types.SymbolInformation list = []

    let collect (identifier: SyntaxToken) kind =
        let location: Types.Location =
            { Uri = documentUri
              Range = identifier.GetLocation().GetLineSpan().Span
                      |> lspRangeForRoslynLinePosSpan
            }

        let symbol: Types.SymbolInformation =
            { Name = identifier.ToString()
              Kind = kind
              Location = location
              ContainerName = None
            }

        collectedSymbols <- symbol :: collectedSymbols

    member __.GetSymbols() = collectedSymbols |> List.rev |> Array.ofList

    override __.VisitClassDeclaration(node) =
        collect node.Identifier Types.SymbolKind.Class

        base.VisitClassDeclaration(node)

    override __.VisitMethodDeclaration(node) =
        collect node.Identifier Types.SymbolKind.Method

        base.VisitMethodDeclaration(node)


let symbolToLspSymbolInformation (symbol: ISymbol): Types.SymbolInformation =
    let documentUri = "file:///xxx"

    let symbolLocation = symbol.Locations |> Seq.head

    let location: Types.Location =
        { Uri = documentUri
          Range = symbolLocation.GetLineSpan().Span |> lspRangeForRoslynLinePosSpan
        }

    { Name = symbol.Name
      Kind = Types.SymbolKind.File
      Location = location
      ContainerName = None }


let findSymbols (solution: Solution) pattern (limit: int option): Async<Types.SymbolInformation list> = async {
    let mutable symbolsFound = []

    for project in solution.Projects do
        let! symbols = SymbolFinder.FindSourceDeclarationsWithPatternAsync(
                           project, pattern, SymbolFilter.TypeAndMember)
                       |> Async.AwaitTask
        symbolsFound <- (List.ofSeq symbols) @ symbolsFound

    return Seq.map symbolToLspSymbolInformation symbolsFound |> List.ofSeq
}

let refactoringProviderInstances =
    let assemblies =
        [ "Microsoft.CodeAnalysis.Features"
          "Microsoft.CodeAnalysis.CSharp.Features"
          "Microsoft.CodeAnalysis.Workspaces"
        ]
        |> Seq.map Assembly.Load
        |> Array.ofSeq

    let validType (t: Type) =
        (not (t.GetTypeInfo().IsInterface))
        && (not (t.GetTypeInfo().IsAbstract))
        && (not (t.GetTypeInfo().ContainsGenericParameters))

    let types =
        assemblies
        |> Seq.collect (fun a -> a.GetTypes())
        |> Seq.filter validType
        |> Seq.toArray

    let isCodeRefactoringProvider (t: Type) = t.IsAssignableTo(typeof<CodeRefactoringProvider>)

    let hasParameterlessConstructor (t: Type) = t.GetConstructor([| |]) <> null

    let validProvider (t: Type) =
        ((string t) <> "Microsoft.CodeAnalysis.ChangeSignature.ChangeSignatureCodeRefactoringProvider")

    types
        |> Seq.filter isCodeRefactoringProvider
        |> Seq.filter hasParameterlessConstructor
        |> Seq.filter validProvider
        |> Seq.map Activator.CreateInstance
        |> Seq.filter (fun i -> i <> null)
        |> Seq.map (fun i -> i :?> CodeRefactoringProvider)
        |> Seq.toArray

let loadSolutionOnDir logMessage dir = async {
    logMessage ("in deferredInitialize, determining solutions on project root: " + dir + "..")

    let fileNotOnNodeModules (filename: string) =
        filename.Split(Path.DirectorySeparatorChar)
        |> Seq.contains "node_modules"
        |> not

    let firstSolutionOnDir dir =
        let solutionFiles =
            Directory.GetFiles(dir, "*.sln", SearchOption.AllDirectories)
            |> Seq.filter fileNotOnNodeModules
            |> Seq.toList

        match solutionFiles with
        | [x] -> Some x
        | _ -> None

    match firstSolutionOnDir(dir) with
    | None ->
        logMessage ("no or multiple .sln files found on " + dir)
        logMessage ("looking for .csproj files on " + dir + "..")

        let csprojFiles =
            Directory.GetFiles(dir, "*.csproj", SearchOption.AllDirectories)
            |> Seq.filter fileNotOnNodeModules
            |> Seq.toList

        if csprojFiles.Length = 0 then
            logMessage ("no or .csproj or sln files found on " + dir)
            ("no or .csproj or sln files found on " + dir) |> Exception |> raise

        let msbuildWorkspace = MSBuildWorkspace.Create()
        msbuildWorkspace.LoadMetadataForReferencedProjects <- true

        for file in csprojFiles do
            logMessage ("loading csproj file " + file + "..")
            let! _ = msbuildWorkspace.OpenProjectAsync(file) |> Async.AwaitTask
            ()

        logMessage "in deferredInitialize, ok project files loaded"

        for diag in msbuildWorkspace.Diagnostics do
            logMessage ("msbuildWorkspace.Diagnostics: " + diag.ToString())

        //workspace <- Some(msbuildWorkspace :> Workspace)
        return Some msbuildWorkspace.CurrentSolution

    | Some solutionPath ->
        try
            logMessage ("in deferredInitialize, loading solution: " + solutionPath)

            let msbuildWorkspace = MSBuildWorkspace.Create()
            msbuildWorkspace.LoadMetadataForReferencedProjects <- true
            let! _ = msbuildWorkspace.OpenSolutionAsync(solutionPath) |> Async.AwaitTask

            logMessage "in deferredInitialize, ok solution loaded"

            for diag in msbuildWorkspace.Diagnostics do
                logMessage ("msbuildWorkspace.Diagnostics: " + diag.ToString())

            //workspace <- Some(msbuildWorkspace :> Workspace)
            return Some msbuildWorkspace.CurrentSolution
        with
        | ex ->
            logMessage ("deferredInitialize failed with " + ex.ToString())
            return None
}
