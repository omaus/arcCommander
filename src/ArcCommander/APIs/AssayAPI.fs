﻿namespace ArcCommander.APIs

open System.IO

open ArcCommander
open ArcCommander.ArgumentProcessing

open ISADotNet
open ISADotNet.XLSX
open ISADotNet.XLSX.AssayFile.MetaData

open FSharpSpreadsheetML
open DocumentFormat.OpenXml.Packaging
open DocumentFormat.OpenXml.Spreadsheet

module Worksheet =

    let setSheetData (sheetData : SheetData) (worksheet : Worksheet) =
        if Worksheet.hasSheetData worksheet then
            worksheet.RemoveChild(Worksheet.getSheetData worksheet)
            |> ignore
        Worksheet.addSheetData sheetData worksheet


//
// ISADotNet lacks some functionality missing here. 
// Unfortunately it cannot be updated right now without breaking the version continuity, 
// as some breaking changes depend on the new Swate version being updated. 
// Until then these helper functions are parked here
//

///let doc = Spreadsheet.fromFile path true  
///  
///MetadataSheet.overwriteWithAssayInfo "Investigation" testAssay2 doc
///
///MetadataSheet.overwriteWithPersons "Investigation" [person] doc
/// 
///MetadataSheet.getPersons "Investigation" doc
///
///MetadataSheet.tryGetAssay "Investigation" doc
///  
///doc.Close()

module MetadataSheet = 

    /// Append an assay metadata sheet with the given sheetname to an existing assay file excel spreadsheet
    let init sheetName assay (doc : SpreadsheetDocument) = 

        let sheet = SheetData.empty()

        let worksheetComment = Comment.create None (Some "Worksheet") None
        let personWithComment = Person.create None None None None None None None None None None (Some [worksheetComment])
        
        toRows assay [personWithComment]
        |> Seq.fold (fun s r -> 
            SheetData.appendRow r s
        ) sheet
        |> ignore

        doc
        |> Spreadsheet.getWorkbookPart
        |> WorkbookPart.appendSheet sheetName sheet
        |> ignore 

        doc

    /// Replace the sheetdata of the sheet with the given sheetname
    let private replaceSheetData (sheetName : string) (data : SheetData) (workbookPart : WorkbookPart) =

        let workbook = Workbook.getOrInit  workbookPart
    
        let sheets = Sheet.Sheets.getOrInit workbook
        let id = 
            sheets |> Sheet.Sheets.getSheets
            |> Seq.find (fun sheet -> Sheet.getName sheet = sheetName)
            |> Sheet.getID

        WorkbookPart.getWorksheetPartById id workbookPart
        |> Worksheet.getOrInit
        |> Worksheet.setSheetData data
        |> ignore 

        workbookPart

    /// Try get assay from metadatasheet with given sheetName
    let tryGetAssay sheetName (doc : SpreadsheetDocument) = 
        match Spreadsheet.tryGetSheetBySheetName sheetName doc with
        | Some sheet -> 
            sheet
            |> SheetData.getRows
            |> fromRows
            |> fun (a,p) ->
                a
        | None -> failwithf "Metadata sheetname %s could not be found" sheetName

    /// Try get persons from metadatasheet with given sheetName
    let getPersons sheetName (doc : SpreadsheetDocument) = 
        match Spreadsheet.tryGetSheetBySheetName sheetName doc with
        | Some sheet -> 
            sheet
            |> SheetData.getRows
            |> fromRows
            |> fun (a,p) ->
                p
        | None -> failwithf "Metadata sheetname %s could not be found" sheetName

    /// Replaces assay metadata from metadatasheet with given sheetName
    let overwriteWithAssayInfo sheetName assay (doc : SpreadsheetDocument) = 

        let workBookPart = Spreadsheet.getWorkbookPart doc
        let newSheet = SheetData.empty()
        
        match Spreadsheet.tryGetSheetBySheetName sheetName doc with
        | Some sheet -> 
            sheet
            |> SheetData.getRows
            |> fromRows
            |> fun (_,p) ->
            
                toRows assay p
                |> Seq.fold (fun s r -> 
                    SheetData.appendRow r s
                ) newSheet
                |> fun s -> replaceSheetData sheetName s workBookPart
        | None -> failwithf "Metadata sheetname %s could not be found" sheetName
        |> ignore

        doc.Save() 

    /// Replaces persons from metadatasheet with given sheetName
    let overwriteWithPersons sheetName persons (doc : SpreadsheetDocument) = 

        let workBookPart = Spreadsheet.getWorkbookPart doc
        let newSheet = SheetData.empty()
        
        match Spreadsheet.tryGetSheetBySheetName sheetName doc with
        | Some sheet -> 
            sheet
            |> SheetData.getRows
            |> fromRows
            |> fun (a,_) ->            
                toRows (Option.defaultValue Assay.empty a) persons
                |> Seq.fold (fun s r -> 
                    SheetData.appendRow r s
                ) newSheet
                |> fun s -> replaceSheetData sheetName s workBookPart
        | None -> failwithf "Metadata sheetname %s could not be found" sheetName
        |> ignore

        doc.Save() 

module AssayFile =

    /// AssayFile.initToPath "Investigation" "lel" testAssay path
    let initToPath metadataSheetName assayIdentifier assay path =
        Spreadsheet.initWithSST assayIdentifier path
        |> MetadataSheet.init metadataSheetName assay
        |> Spreadsheet.close






/// ArcCommander Assay API functions that get executed by the assay focused subcommand verbs
module AssayAPI =        


    module AssayFolder =
        
        let exists (arcConfiguration : ArcConfiguration) (identifier : string) =
            AssayConfiguration.getFolderPath identifier arcConfiguration
            |> System.IO.Directory.Exists

    module AssayFile =
        
        let exists (arcConfiguration : ArcConfiguration) (identifier : string) =
            IsaModelConfiguration.getAssayFilePath identifier arcConfiguration
            |> System.IO.File.Exists
        
        let create (arcConfiguration : ArcConfiguration) (identifier : string) =
            IsaModelConfiguration.getAssayFilePath identifier arcConfiguration
            |> ISADotNet.XLSX.AssayFile.AssayFile.init "Investigation" identifier

    /// Initializes a new empty assay file and associated folder structure in the arc.
    let init (arcConfiguration : ArcConfiguration) (assayArgs : Map<string,Argument>) =

        let verbosity = GeneralConfiguration.getVerbosity arcConfiguration
        
        if verbosity >= 1 then printfn "Start Assay Init"

        let name = getFieldValueByName "AssayIdentifier" assayArgs

        if AssayFolder.exists arcConfiguration name then
            if verbosity >= 1 then printfn "Assay folder with identifier %s already exists" name
        else
            AssayConfiguration.getSubFolderPaths name arcConfiguration
            |> Array.iter (Directory.CreateDirectory >> ignore)

            AssayFile.create arcConfiguration name 

            AssayConfiguration.getFilePaths name arcConfiguration
            |> Array.iter (File.Create >> ignore)


    /// Updates an existing assay file in the ARC with the given assay metadata contained in cliArgs.
    let update (arcConfiguration : ArcConfiguration) (assayArgs : Map<string,Argument>) =
        
        let verbosity = GeneralConfiguration.getVerbosity arcConfiguration

        if verbosity >= 1 then printfn "Start Assay Update"

        let updateOption = if containsFlag "ReplaceWithEmptyValues" assayArgs then API.Update.UpdateAll else API.Update.UpdateByExisting            

        let assayIdentifier = getFieldValueByName "AssayIdentifier" assayArgs

        let assayFileName = 
            IsaModelConfiguration.tryGetAssayFileName assayIdentifier arcConfiguration
            |> Option.get

        let assay = 
            Assays.fromString
                (getFieldValueByName  "MeasurementType" assayArgs)
                (getFieldValueByName  "MeasurementTypeTermAccessionNumber" assayArgs)
                (getFieldValueByName  "MeasurementTypeTermSourceREF" assayArgs)
                (getFieldValueByName  "TechnologyType" assayArgs)
                (getFieldValueByName  "TechnologyTypeTermAccessionNumber" assayArgs)
                (getFieldValueByName  "TechnologyTypeTermSourceREF" assayArgs)
                (getFieldValueByName  "TechnologyPlatform" assayArgs)
                assayFileName
                []

        let studyIdentifier = 
            match getFieldValueByName "StudyIdentifier" assayArgs with
            | "" -> assayIdentifier
            | s -> 
                if verbosity >= 2 then printfn "No Study Identifier given, use assayIdentifier instead"
                s

        let investigationFilePath = IsaModelConfiguration.tryGetInvestigationFilePath arcConfiguration |> Option.get
        
        let investigation = Investigation.fromFile investigationFilePath


        match investigation.Studies with
        | Some studies -> 
            match API.Study.tryGetByIdentifier studyIdentifier studies with
            | Some study -> 
                match study.Assays with
                | Some assays -> 
                    if API.Assay.existsByFileName assayFileName assays then
                        API.Assay.updateByFileName updateOption assay assays
                        |> API.Study.setAssays study
                    else
                        if verbosity >= 1 then printfn "Assay with the identifier %s does not exist in the study with the identifier %s" assayIdentifier studyIdentifier
                        if containsFlag "AddIfMissing" assayArgs then
                            if verbosity >= 1 then printfn "Registering assay as AddIfMissing Flag was set" 
                            API.Assay.add assays assay
                            |> API.Study.setAssays study
                        else 
                            if verbosity >= 2 then printfn "AddIfMissing argument can be used to register assay with the update command if it is missing" 
                            study
                | None -> 
                    if verbosity >= 1 then printfn "The study with the identifier %s does not contain any assays" studyIdentifier
                    if containsFlag "AddIfMissing" assayArgs then
                        if verbosity >= 1 then printfn "Registering assay as AddIfMissing Flag was set" 
                        [assay]
                        |> API.Study.setAssays study
                    else 
                        if verbosity >= 2 then printfn "AddIfMissing argument can be used to register assay with the update command if it is missing" 
                        study
                |> fun s -> API.Study.updateByIdentifier API.Update.UpdateAll s studies
                |> API.Investigation.setStudies investigation
            | None -> 
                if verbosity >= 1 then printfn "Study with the identifier %s does not exist in the investigation file" studyIdentifier              
                investigation
        | None -> 
            if verbosity >= 1 then printfn "The investigation does not contain any studies"  
            investigation
        |> Investigation.toFile investigationFilePath
        

    /// Opens an existing assay file in the ARC with the text editor set in globalArgs, additionally setting the given assay metadata contained in assayArgs.
    let edit (arcConfiguration : ArcConfiguration) (assayArgs : Map<string,Argument>) =
        
        let verbosity = GeneralConfiguration.getVerbosity arcConfiguration

        if verbosity >= 1 then printfn "Start Assay Edit"

        let editor = GeneralConfiguration.getEditor arcConfiguration
        let workDir = GeneralConfiguration.getWorkDirectory arcConfiguration

        let assayIdentifier = getFieldValueByName "AssayIdentifier" assayArgs
        
        let assayFileName = 
            IsaModelConfiguration.tryGetAssayFileName assayIdentifier arcConfiguration
            |> Option.get

        let studyIdentifier = 
            match getFieldValueByName "StudyIdentifier" assayArgs with
            | "" -> assayIdentifier
            | s -> 
                if verbosity >= 2 then printfn "No Study Identifier given, use assayIdentifier instead"
                s

        let investigationFilePath = IsaModelConfiguration.tryGetInvestigationFilePath arcConfiguration |> Option.get
        
        let investigation = Investigation.fromFile investigationFilePath

        
        match investigation.Studies with
        | Some studies -> 
            match API.Study.tryGetByIdentifier studyIdentifier studies with
            | Some study -> 
                match study.Assays with
                | Some assays -> 
                    match API.Assay.tryGetByFileName assayFileName assays with
                    | Some assay ->
                
                        ArgumentProcessing.Prompt.createIsaItemQuery editor workDir 
                            (List.singleton >> Assays.writeAssays None) 
                            (Assays.readAssays None 1 >> fun (_,_,_,items) -> items.Head) 
                            assay
                        |> fun a -> API.Assay.updateBy ((=) assay) API.Update.UpdateAll a assays
                        |> API.Study.setAssays study
                        |> fun s -> API.Study.updateByIdentifier API.Update.UpdateAll s studies
                        |> API.Investigation.setStudies investigation

                    | None ->
                        if verbosity >= 1 then printfn "Assay with the identifier %s does not exist in the study with the identifier %s" assayIdentifier studyIdentifier
                        investigation
                | None -> 
                    if verbosity >= 1 then printfn "The study with the identifier %s does not contain any assays" studyIdentifier
                    investigation
            | None -> 
                if verbosity >= 1 then printfn "Study with the identifier %s does not exist in the investigation file" studyIdentifier
                investigation
        | None -> 
            if verbosity >= 1 then printfn "The investigation does not contain any studies"  
            investigation
        |> Investigation.toFile investigationFilePath


    /// Registers an existing assay in the ARC's investigation file with the given assay metadata contained in assayArgs.
    let register (arcConfiguration : ArcConfiguration) (assayArgs : Map<string,Argument>) =

        let verbosity = GeneralConfiguration.getVerbosity arcConfiguration
        
        if verbosity >= 1 then printfn "Start Assay Register"

        let assayIdentifier = getFieldValueByName "AssayIdentifier" assayArgs
        
        let assayFileName = 
            IsaModelConfiguration.tryGetAssayFileName assayIdentifier arcConfiguration
            |> Option.get
        
        let assay = 
            Assays.fromString
                (getFieldValueByName  "MeasurementType" assayArgs)
                (getFieldValueByName  "MeasurementTypeTermAccessionNumber" assayArgs)
                (getFieldValueByName  "MeasurementTypeTermSourceREF" assayArgs)
                (getFieldValueByName  "TechnologyType" assayArgs)
                (getFieldValueByName  "TechnologyTypeTermAccessionNumber" assayArgs)
                (getFieldValueByName  "TechnologyTypeTermSourceREF" assayArgs)
                (getFieldValueByName  "TechnologyPlatform" assayArgs)
                assayFileName
                []
               
        let studyIdentifier = 
            match getFieldValueByName "StudyIdentifier" assayArgs with
            | "" -> 
                if verbosity >= 1 then printfn "No Study Identifier given, use assayIdentifier instead"
                assayIdentifier
            | s -> s

        let investigationFilePath = IsaModelConfiguration.tryGetInvestigationFilePath arcConfiguration |> Option.get
        
        let investigation = Investigation.fromFile investigationFilePath
                
        match investigation.Studies with
        | Some studies -> 
            match API.Study.tryGetByIdentifier studyIdentifier studies with
            | Some study -> 
                match study.Assays with
                | Some assays -> 
                    match API.Assay.tryGetByFileName assayFileName assays with
                    | Some assay ->
                        if verbosity >= 1 then printfn "Assay with the identifier %s already exists in the investigation file" assayIdentifier
                        assays
                    | None ->                       
                        API.Assay.add assays assay                     
                | None ->
                    [assay]
                |> API.Study.setAssays study
                |> fun s -> API.Study.updateByIdentifier API.Update.UpdateAll s studies
                
            | None ->
                if verbosity >= 1 then printfn "Study with the identifier %s does not exist yet, creating it now" studyIdentifier
                if StudyAPI.StudyFile.exists arcConfiguration studyIdentifier |> not then
                    StudyAPI.StudyFile.create arcConfiguration studyIdentifier
                let info = Study.StudyInfo.create studyIdentifier "" "" "" "" "" []
                Study.fromParts info [] [] [] [assay] [] []
                |> API.Study.add studies
        | None ->
            if verbosity >= 1 then printfn "Study with the identifier %s does not exist yet, creating it now" studyIdentifier
            if StudyAPI.StudyFile.exists arcConfiguration studyIdentifier |> not then
                StudyAPI.StudyFile.create arcConfiguration studyIdentifier
            let info = Study.StudyInfo.create studyIdentifier "" "" "" "" "" []
            [Study.fromParts info [] [] [] [assay] [] []]
        |> API.Investigation.setStudies investigation
        |> Investigation.toFile investigationFilePath
    
    /// Creates a new assay file and associated folder structure in the arc and registers it in the ARC's investigation file with the given assay metadata contained in assayArgs.
    let add (arcConfiguration : ArcConfiguration) (assayArgs : Map<string,Argument>) =

        init arcConfiguration assayArgs
        register arcConfiguration assayArgs

    /// Unregisters an assay file from the ARC's investigation file assay register.
    let unregister (arcConfiguration : ArcConfiguration) (assayArgs : Map<string,Argument>) =

        let verbosity = GeneralConfiguration.getVerbosity arcConfiguration
        
        if verbosity >= 1 then printfn "Start Assay Unregister"

        let assayIdentifier = getFieldValueByName "AssayIdentifier" assayArgs

        let assayFileName = 
            IsaModelConfiguration.tryGetAssayFileName assayIdentifier arcConfiguration
            |> Option.get

        let studyIdentifier = 
            match getFieldValueByName "StudyIdentifier" assayArgs with
            | "" -> assayIdentifier
            | s -> 
                if verbosity >= 2 then printfn "No Study Identifier given, use assayIdentifier instead"
                s

        let investigationFilePath = IsaModelConfiguration.tryGetInvestigationFilePath arcConfiguration |> Option.get
        
        let investigation = Investigation.fromFile investigationFilePath

        match investigation.Studies with
        | Some studies -> 
            match API.Study.tryGetByIdentifier studyIdentifier studies with
            | Some study -> 
                match study.Assays with
                | Some assays -> 
                    match API.Assay.tryGetByFileName assayFileName assays with
                    | Some assay ->
                        API.Assay.removeByFileName assayFileName assays
                        |> API.Study.setAssays study
                        |> fun s -> API.Study.updateByIdentifier API.Update.UpdateAll s studies
                        |> API.Investigation.setStudies investigation
                    | None ->
                        if verbosity >= 1 then printfn "Assay with the identifier %s does not exist in the study with the identifier %s" assayIdentifier studyIdentifier
                        investigation
                | None -> 
                    if verbosity >= 1 then printfn "The study with the identifier %s does not contain any assays" studyIdentifier
                    investigation
            | None -> 
                if verbosity >= 1 then printfn "Study with the identifier %s does not exist in the investigation file" studyIdentifier
                investigation
        | None -> 
            if verbosity >= 1 then printfn "The investigation does not contain any studies"  
            investigation
        |> Investigation.toFile investigationFilePath
    
    /// Deletes assay folder and underlying file structure of given assay.
    let delete (arcConfiguration : ArcConfiguration) (assayArgs : Map<string,Argument>) =

        let verbosity = GeneralConfiguration.getVerbosity arcConfiguration
        
        if verbosity >= 1 then printfn "Start Assay Delete"

        let assayIdentifier = getFieldValueByName "AssayIdentifier" assayArgs

        let assayFolder = 
            AssayConfiguration.tryGetFolderPath assayIdentifier arcConfiguration
            |> Option.get

        if System.IO.Directory.Exists(assayFolder) then
            System.IO.Directory.Delete(assayFolder,true)

    /// Remove an assay from the ARC by both unregistering it from the investigation file and removing its folder with the underlying file structure.
    let remove (arcConfiguration : ArcConfiguration) (assayArgs : Map<string,Argument>) =
        unregister arcConfiguration assayArgs
        delete arcConfiguration assayArgs

    /// Moves an assay file from one study group to another (provided by assayArgs)
    let move (arcConfiguration : ArcConfiguration) (assayArgs : Map<string,Argument>) =

        let verbosity = GeneralConfiguration.getVerbosity arcConfiguration
        
        if verbosity >= 1 then printfn "Start Assay Move"

        let assayIdentifier = getFieldValueByName "AssayIdentifier" assayArgs
        let assayFileName = IsaModelConfiguration.tryGetAssayFileName assayIdentifier arcConfiguration |> Option.get

        let studyIdentifier = getFieldValueByName "StudyIdentifier" assayArgs
        let targetStudyIdentifer = getFieldValueByName "TargetStudyIdentifier" assayArgs

        let investigationFilePath = IsaModelConfiguration.tryGetInvestigationFilePath arcConfiguration |> Option.get      
        let investigation = Investigation.fromFile investigationFilePath
        
        match investigation.Studies with
        | Some studies -> 
            match API.Study.tryGetByIdentifier studyIdentifier studies with
            | Some study -> 
                match study.Assays with
                | Some assays -> 
                    match API.Assay.tryGetByFileName assayFileName assays with
                    | Some assay ->
                
                        let studies = 
                            // Remove Assay from old study
                            API.Study.mapAssays (API.Assay.removeByFileName assayFileName) study
                            |> fun s -> API.Study.updateByIdentifier API.Update.UpdateAll s studies

                        match API.Study.tryGetByIdentifier targetStudyIdentifer studies with
                        | Some targetStudy -> 
                            API.Study.mapAssays (fun assays -> API.Assay.add assays assay) targetStudy
                            |> fun s -> API.Study.updateByIdentifier API.Update.UpdateAll s studies
                            |> API.Investigation.setStudies investigation
                        | None -> 
                            if verbosity >= 2 then printfn "Target Study with the identifier %s does not exist in the investigation file, creating new study to move assay to" studyIdentifier
                            let info = Study.StudyInfo.create targetStudyIdentifer "" "" "" "" "" []
                            Study.fromParts info [] [] [] [assay] [] []
                            |> API.Study.add studies
                            |> API.Investigation.setStudies investigation
                    | None -> 
                        if verbosity >= 1 then printfn "Assay with the identifier %s does not exist in the study with the identifier %s" assayIdentifier studyIdentifier
                        investigation
                | None -> 
                    if verbosity >= 1 then printfn "The study with the identifier %s does not contain any assays" studyIdentifier
                    investigation
            | None -> 
                if verbosity >= 1 then printfn "Study with the identifier %s does not exist in the investigation file" studyIdentifier
                investigation
        | None -> 
            if verbosity >= 1 then printfn "The investigation does not contain any studies"  
            investigation
        |> Investigation.toFile investigationFilePath

    /// Moves an assay file from one study group to another (provided by assayArgs).
    let get (arcConfiguration : ArcConfiguration) (assayArgs : Map<string,Argument>) =
     
        let verbosity = GeneralConfiguration.getVerbosity arcConfiguration
        
        if verbosity >= 1 then printfn "Start Assay Get"

        let assayIdentifier = getFieldValueByName "AssayIdentifier" assayArgs

        let assayFileName = IsaModelConfiguration.tryGetAssayFileName assayIdentifier arcConfiguration |> Option.get

        let studyIdentifier = 
            match getFieldValueByName "StudyIdentifier" assayArgs with
            | "" -> assayIdentifier
            | s -> 
                if verbosity >= 2 then printfn "No Study Identifier given, use assayIdentifier instead"
                s

        let investigationFilePath = IsaModelConfiguration.tryGetInvestigationFilePath arcConfiguration |> Option.get
        
        let investigation = Investigation.fromFile investigationFilePath
        
        match investigation.Studies with
        | Some studies -> 
            match API.Study.tryGetByIdentifier studyIdentifier studies with
            | Some study -> 
                match study.Assays with
                | Some assays -> 
                    match API.Assay.tryGetByFileName assayFileName assays with
                    | Some assay ->
                        [assay]
                        |> Prompt.serializeXSLXWriterOutput (Assays.writeAssays None)
                        |> printfn "%s"
                    | None -> 
                        if verbosity >= 1 then printfn "Assay with the identifier %s does not exist in the study with the identifier %s" assayIdentifier studyIdentifier
                | None -> 
                    if verbosity >= 1 then printfn "The study with the identifier %s does not contain any assays" studyIdentifier                   
            | None -> 
                if verbosity >= 1 then printfn "Study with the identifier %s does not exist in the investigation file" studyIdentifier
        | None -> 
            if verbosity >= 1 then printfn "The investigation does not contain any studies"    



    /// Lists all assay identifiers registered in this investigation.
    let list (arcConfiguration : ArcConfiguration) =

        let verbosity = GeneralConfiguration.getVerbosity arcConfiguration
        
        if verbosity >= 1 then printfn "Start Assay List"
        
        let investigationFilePath = IsaModelConfiguration.tryGetInvestigationFilePath arcConfiguration |> Option.get

        let investigation = Investigation.fromFile investigationFilePath

        match investigation.Studies with
        | Some studies -> 
            studies
            |> List.iter (fun study ->
                let studyIdentifier = Option.defaultValue "" study.Identifier
                match study.Assays with
                | Some assays -> 
                    if List.isEmpty assays |> not then
                        printfn "Study: %s" studyIdentifier
                        assays 
                        |> Seq.iter (fun assay -> printfn "--Assay: %s" (Option.defaultValue "" assay.FileName))
                | None -> 
                    if verbosity >= 1 then printfn "The study with the identifier %s does not contain any assays" studyIdentifier   
            )
        | None -> 
            if verbosity >= 1 then printfn "The investigation does not contain any studies"  

    /// Export an assay to json.
    let exportSingleAssay (arcConfiguration : ArcConfiguration) (assayArgs : Map<string,Argument>) =

        let verbosity = GeneralConfiguration.getVerbosity arcConfiguration
        
        if verbosity >= 1 then printfn "Start exporting single assay"
        
        let assayIdentifier = getFieldValueByName "AssayIdentifier" assayArgs
        
        let assayFileName = IsaModelConfiguration.tryGetAssayFileName assayIdentifier arcConfiguration |> Option.get
        
        let assayFilePath = IsaModelConfiguration.getAssayFilePath assayIdentifier arcConfiguration

        let studyIdentifier = 
            match getFieldValueByName "StudyIdentifier" assayArgs with
            | "" -> assayIdentifier
            | s -> 
                if verbosity >= 2 then printfn "No Study Identifier given, use assayIdentifier instead"
                s
        
        let investigationFilePath = IsaModelConfiguration.tryGetInvestigationFilePath arcConfiguration |> Option.get
                
        let investigation = Investigation.fromFile investigationFilePath

        // Try retrieve given assay from investigation file
        let assayInInvestigation = 
            match investigation.Studies with
            | Some studies -> 
                match API.Study.tryGetByIdentifier studyIdentifier studies with
                | Some study -> 
                    match study.Assays with
                    | Some assays -> 
                        match API.Assay.tryGetByFileName assayFileName assays with
                        | Some assay ->
                            Some assay                           
                        | None -> 
                            if verbosity >= 1 then printfn "Assay with the identifier %s does not exist in the study with the identifier %s" assayIdentifier studyIdentifier
                            None
                    | None -> 
                        if verbosity >= 1 then printfn "The study with the identifier %s does not contain any assays" studyIdentifier                   
                        None
                | None -> 
                    if verbosity >= 1 then printfn "Study with the identifier %s does not exist in the investigation file" studyIdentifier
                    None
            | None -> 
                if verbosity >= 1 then printfn "The investigation does not contain any studies"     
                None

        let persons,assayFromFile =

            if System.IO.File.Exists assayFilePath then
                try
                    let _,_,p,a = AssayFile.AssayFile.fromFile assayFilePath
                    p, Some a
                with
                | err -> 
                    if verbosity >= 1 then printfn "Assay file \"%s\" could not be read" assayFilePath    
                    [],None
            else
                if verbosity >= 1 then printfn "Assay file \"%s\" does not exist" assayFilePath     
                [],None
        
        let mergedAssay = 
            match assayInInvestigation,assayFromFile with
            | Some ai, Some a -> API.Update.UpdateByExisting.updateRecordType ai a
            | None, Some a -> a
            | Some ai, None -> ai
            | None, None -> failwith "No assay could be retrieved"     
          
          
        if containsFlag "ProcessSequence" assayArgs then

            let output = mergedAssay.ProcessSequence |> Option.defaultValue []

            match tryGetFieldValueByName "Path" assayArgs with
            | Some p -> ArgumentProcessing.serializeToFile p output
            | None -> ()

            System.Console.Write(ArgumentProcessing.serializeToString output)

        else 

            let output = Study.create None None None None None None None None (API.Option.fromValueWithDefault [] persons) None None None None (Some [mergedAssay]) None None None None
     
            match tryGetFieldValueByName "Path" assayArgs with
            | Some p -> ISADotNet.Json.Study.toFile p output
            | None -> ()

            System.Console.Write(ISADotNet.Json.Study.toString output)


    /// Export all assays to json.
    let exportAllAssays (arcConfiguration : ArcConfiguration) (assayArgs : Map<string,Argument>) =

        let verbosity = GeneralConfiguration.getVerbosity arcConfiguration
        
        if verbosity >= 1 then printfn "Start exporting all assays"
        
        let investigationFilePath = IsaModelConfiguration.tryGetInvestigationFilePath arcConfiguration |> Option.get
        
        let investigation = Investigation.fromFile investigationFilePath

        let assayIdentifiers = AssayConfiguration.getAssayNames arcConfiguration
        
        let assays =
            assayIdentifiers
            |> Array.toList
            |> List.map (fun assayIdentifier ->

                let assayFileName = IsaModelConfiguration.tryGetAssayFileName assayIdentifier arcConfiguration |> Option.get
        
                let assayFilePath = IsaModelConfiguration.getAssayFilePath assayIdentifier arcConfiguration

                let studyIdentifier = 
                    match getFieldValueByName "StudyIdentifier" assayArgs with
                    | "" -> assayIdentifier
                    | s -> 
                        if verbosity >= 2 then printfn "No Study Identifier given, use assayIdentifier instead"
                        s
              
                // Try retrieve given assay from investigation file
                let assayInInvestigation = 
                    match investigation.Studies with
                    | Some studies -> 
                        match API.Study.tryGetByIdentifier studyIdentifier studies with
                        | Some study -> 
                            match study.Assays with
                            | Some assays -> 
                                match API.Assay.tryGetByFileName assayFileName assays with
                                | Some assay ->
                                    Some assay                           
                                | None -> 
                                    if verbosity >= 1 then printfn "Assay with the identifier %s does not exist in the study with the identifier %s" assayIdentifier studyIdentifier
                                    None
                            | None -> 
                                if verbosity >= 1 then printfn "The study with the identifier %s does not contain any assays" studyIdentifier                   
                                None
                        | None -> 
                            if verbosity >= 1 then printfn "Study with the identifier %s does not exist in the investigation file" studyIdentifier
                            None
                    | None -> 
                        if verbosity >= 1 then printfn "The investigation does not contain any studies"     
                        None

                let persons,assayFromFile =

                    if System.IO.File.Exists assayFilePath then
                        try
                            let _,_,p,a = AssayFile.AssayFile.fromFile assayFilePath
                            p, Some a
                        with
                        | err -> 
                            if verbosity >= 1 then printfn "Assay file \"%s\" could not be read" assayFilePath    
                            [],None
                    else
                        if verbosity >= 1 then printfn "Assay file \"%s\" does not exist" assayFilePath     
                        [],None
        
                let mergedAssay = 
                    match assayInInvestigation,assayFromFile with
                    | Some ai, Some a -> API.Update.UpdateByExisting.updateRecordType ai a
                    | None, Some a -> a
                    | Some ai, None -> ai
                    | None, None -> failwith "No assay could be retrieved"     
            
                Study.create None None None None None None None None (API.Option.fromValueWithDefault [] persons) None None None None (Some [mergedAssay]) None None None None
            )
        
          
        if containsFlag "ProcessSequence" assayArgs then

            let output = 
                assays 
                |> List.collect (fun s -> 
                    s.Assays 
                    |> Option.defaultValue [] 
                    |> List.collect (fun a -> a.ProcessSequence |> Option.defaultValue [])
                )
                                                          
            match tryGetFieldValueByName "Path" assayArgs with
            | Some p -> ArgumentProcessing.serializeToFile p output
            | None -> ()

            System.Console.Write(ArgumentProcessing.serializeToString output)

        else 

            match tryGetFieldValueByName "Path" assayArgs with
            | Some p -> ArgumentProcessing.serializeToFile p assays
            | None -> ()

            System.Console.Write(ArgumentProcessing.serializeToString assays)

    /// Export an assay to json.
    let export (arcConfiguration : ArcConfiguration) (assayArgs : Map<string,Argument>) =

        let verbosity = GeneralConfiguration.getVerbosity arcConfiguration
        
        if verbosity >= 1 then printfn "Start Assay export"

        match tryGetFieldValueByName "AssayIdentifier" assayArgs with
        | Some _ -> exportSingleAssay arcConfiguration assayArgs
        | None -> exportAllAssays arcConfiguration assayArgs


    /// Functions for altering investigation contacts
    module Contacts =

        /// Updates an existing person in the ARC investigation study with the given person metadata contained in cliArgs.
        let update (arcConfiguration:ArcConfiguration) (personArgs : Map<string,Argument>) =

            printfn "Not implemented yet."

            //let verbosity = GeneralConfiguration.getVerbosity arcConfiguration

            //if verbosity >= 1 then printfn "Start Person Update"

            //let updateOption = if containsFlag "ReplaceWithEmptyValues" personArgs then API.Update.UpdateAll else API.Update.UpdateByExisting            

            //let lastName    = getFieldValueByName "LastName"    personArgs                   
            //let firstName   = getFieldValueByName "FirstName"   personArgs
            //let midInitials = getFieldValueByName "MidInitials" personArgs

            //let comments = 
            //    match tryGetFieldValueByName "ORCID" personArgs with
            //    | Some orcid -> [Comment.fromString "Investigation Person ORCID" orcid]
            //    | None -> []

            //let person = 
            //    Contacts.fromString
            //        lastName
            //        firstName
            //        midInitials
            //        (getFieldValueByName  "Email"                       personArgs)
            //        (getFieldValueByName  "Phone"                       personArgs)
            //        (getFieldValueByName  "Fax"                         personArgs)
            //        (getFieldValueByName  "Address"                     personArgs)
            //        (getFieldValueByName  "Affiliation"                 personArgs)
            //        (getFieldValueByName  "Roles"                       personArgs)
            //        (getFieldValueByName  "RolesTermAccessionNumber"    personArgs)
            //        (getFieldValueByName  "RolesTermSourceREF"          personArgs)
            //        comments

            //let investigationFilePath = IsaModelConfiguration.tryGetInvestigationFilePath arcConfiguration |> Option.get
            
            //let investigation = Investigation.fromFile investigationFilePath

            //let studyIdentifier = getFieldValueByName "StudyIdentifier" personArgs

            //match investigation.Studies with
            //| Some studies -> 
            //    match API.Study.tryGetByIdentifier studyIdentifier studies with
            //    | Some study -> 
            //        match study.Contacts with
            //        | Some persons -> 
            //            if API.Person.existsByFullName firstName midInitials lastName persons then
            //                API.Person.updateByFullName updateOption person persons
            //                |> API.Assay.setContacts study

            //            else
            //                if verbosity >= 1 then printfn "Person with the name %s %s %s does not exist in the study with the identifier %s" firstName midInitials lastName studyIdentifier
            //                if containsFlag "AddIfMissing" personArgs then
            //                    if verbosity >= 1 then printfn "Registering person as AddIfMissing Flag was set" 
            //                    API.Person.add persons person
            //                    |> API.Assay.setContacts study
            //                else 
            //                    if verbosity >= 2 then printfn "AddIfMissing argument can be used to register person with the update command if it is missing" 
            //                    study
            //        | None -> 
            //            if verbosity >= 1 then printfn "The study with the identifier %s does not contain any persons" studyIdentifier
            //            if containsFlag "AddIfMissing" personArgs then
            //                if verbosity >= 1 then printfn "Registering person as AddIfMissing Flag was set" 
            //                [person]
            //                |> API.Assay.setContacts study
            //            else 
            //                if verbosity >= 2 then printfn "AddIfMissing argument can be used to register person with the update command if it is missing" 
            //                study
            //        |> fun s -> API.Assay.updateByIdentifier API.Update.UpdateAll s studies
            //        |> API.Investigation.setAssays investigation
            //    | None -> 
            //        if verbosity >= 1 then printfn "Study with the identifier %s does not exist in the investigation file" studyIdentifier
            //        investigation
            //| None -> 
            //    if verbosity >= 1 then printfn "The investigation does not contain any studies"  
            //    investigation
            //|> Investigation.toFile investigationFilePath
        
        /// Opens an existing person by fullname (lastName, firstName, MidInitials) in the ARC investigation study with the text editor set in globalArgs.
        let edit (arcConfiguration : ArcConfiguration) (personArgs : Map<string,Argument>) =

            printfn "Not implemented yet."

            //let verbosity = GeneralConfiguration.getVerbosity arcConfiguration
            
            //if verbosity >= 1 then printfn "Start Person Edit"

            //let editor = GeneralConfiguration.getEditor arcConfiguration
            //let workDir = GeneralConfiguration.getWorkDirectory arcConfiguration

            //let lastName = (getFieldValueByName  "LastName"   personArgs)
            //let firstName = (getFieldValueByName  "FirstName"     personArgs)
            //let midInitials = (getFieldValueByName  "MidInitials"  personArgs)

            //let studyIdentifier = getFieldValueByName "StudyIdentifier" personArgs

            //let investigationFilePath = IsaModelConfiguration.tryGetInvestigationFilePath arcConfiguration |> Option.get
            
            //let investigation = Investigation.fromFile investigationFilePath
            
            //let studies = investigation.Studies

            //// TO DO: Implementation für Assay-Datenstruktur

            //match investigation.Studies with
            //| Some studies -> 
            //    match API.Study.tryGetByIdentifier studyIdentifier studies with
            //    | Some study -> 
            //        match study.Contacts with
            //        | Some persons -> 
            //            match API.Person.tryGetByFullName firstName midInitials lastName persons with
            //            | Some person -> 
            //                ArgumentProcessing.Prompt.createIsaItemQuery editor workDir 
            //                    (List.singleton >> Contacts.writePersons None) 
            //                    (Contacts.readPersons None 1 >> fun (_,_,_,items) -> items.Head) 
            //                    person
            //                |> fun p -> API.Person.updateBy ((=) person) API.Update.UpdateAll p persons
            //                |> API.Study.setContacts study
            //                |> fun s -> API.Assay.updateByIdentifier API.Update.UpdateAll s studies
            //                |> API.Investigation.setAssays investigation
            //            | None ->
            //                if verbosity >= 1 then printfn "Person with the name %s %s %s does not exist in the study with the identifier %s" firstName midInitials lastName studyIdentifier
            //                investigation
            //        | None -> 
            //            if verbosity >= 1 then printfn "The study with the identifier %s does not contain any persons" studyIdentifier
            //            investigation
            //    | None -> 
            //        if verbosity >= 1 then printfn "Study with the identifier %s does not exist in the investigation file" studyIdentifier
            //        investigation
            //| None -> 
            //    if verbosity >= 1 then printfn "The investigation does not contain any studies"  
            //    investigation
            //|> Investigation.toFile investigationFilePath

        /// Registers a person in the ARC investigation study with the given person metadata contained in personArgs.
        let register (arcConfiguration : ArcConfiguration) (personArgs : Map<string,Argument>) =

            printfn "Not implemented yet."

            //let verbosity = GeneralConfiguration.getVerbosity arcConfiguration
            
            //if verbosity >= 1 then printfn "Start Person Register"

            //let lastName    = getFieldValueByName "LastName"    personArgs                   
            //let firstName   = getFieldValueByName "FirstName"   personArgs
            //let midInitials = getFieldValueByName "MidInitials" personArgs

            //let comments = 
            //    match tryGetFieldValueByName "ORCID" personArgs with
            //    | Some orcid -> [Comment.fromString "Investigation Person ORCID" orcid]
            //    | None -> []

            //let person = 
            //    Contacts.fromString
            //        lastName
            //        firstName
            //        midInitials
            //        (getFieldValueByName  "Email"                       personArgs)
            //        (getFieldValueByName  "Phone"                       personArgs)
            //        (getFieldValueByName  "Fax"                         personArgs)
            //        (getFieldValueByName  "Address"                     personArgs)
            //        (getFieldValueByName  "Affiliation"                 personArgs)
            //        (getFieldValueByName  "Roles"                       personArgs)
            //        (getFieldValueByName  "RolesTermAccessionNumber"    personArgs)
            //        (getFieldValueByName  "RolesTermSourceREF"          personArgs)
            //        comments
            
            //let studyIdentifier = getFieldValueByName "StudyIdentifier" personArgs

            //let investigationFilePath = IsaModelConfiguration.tryGetInvestigationFilePath arcConfiguration |> Option.get
            
            //let investigation = Investigation.fromFile investigationFilePath

            //// TO DO: Implementation für Assay-Datenstruktur

            //match investigation.Assays with
            //| Some studies -> 
            //    match API.Study.tryGetByIdentifier studyIdentifier studies with
            //    | Some study -> 
            //        match study.Contacts with
            //        | Some persons -> 
            //            if API.Person.existsByFullName firstName midInitials lastName persons then               
            //                if verbosity >= 1 then printfn "Person with the name %s %s %s already exists in the investigation file" firstName midInitials lastName
            //                persons
            //            else
            //                API.Person.add persons person                           
            //        | None -> 
            //            [person]
            //        |> API.Assay.setContacts study
            //        |> fun s -> API.Assay.updateByIdentifier API.Update.UpdateAll s studies
            //        |> API.Investigation.setStudies investigation
            //    | None ->
            //        printfn "Study with the identifier %s does not exist in the investigation file" studyIdentifier
            //        investigation
            //| None -> 
            //    if verbosity >= 1 then printfn "The investigation does not contain any studies"  
            //    investigation
            //|> Investigation.toFile investigationFilePath
    

        /// Opens an existing person by fullname (lastName, firstName, MidInitials) in the ARC with the text editor set in globalArgs.
        let unregister (arcConfiguration : ArcConfiguration) (personArgs : Map<string,Argument>) =

            printfn "Not implemented yet."

            //let verbosity = GeneralConfiguration.getVerbosity arcConfiguration
            
            //if verbosity >= 1 then printfn "Start Person Unregister"

            //let lastName = (getFieldValueByName  "LastName"   personArgs)
            //let firstName = (getFieldValueByName  "FirstName"     personArgs)
            //let midInitials = (getFieldValueByName  "MidInitials"  personArgs)

            //let studyIdentifier = getFieldValueByName "StudyIdentifier" personArgs

            //let investigationFilePath = IsaModelConfiguration.tryGetInvestigationFilePath arcConfiguration |> Option.get
            
            //let investigation = Investigation.fromFile investigationFilePath

            //// TO DO: Implementation für Assay-Datenstruktur
            
            //match investigation.Studies with
            //| Some studies -> 
            //    match API.Study.tryGetByIdentifier studyIdentifier studies with
            //    | Some study -> 
            //        match study.Contacts with
            //        | Some persons -> 
            //            if API.Person.existsByFullName firstName midInitials lastName persons then
            //                API.Person.removeByFullName firstName midInitials lastName persons
            //                |> API.Study.setContacts study
            //                |> fun s -> API.Assay.updateByIdentifier API.Update.UpdateAll s studies
            //                |> API.Investigation.setAssays investigation
            //            else
            //                if verbosity >= 1 then printfn "Person with the name %s %s %s  does not exist in the study with the identifier %s" firstName midInitials lastName studyIdentifier
            //                investigation
            //        | None -> 
            //            if verbosity >= 1 then printfn "The study with the identifier %s does not contain any persons" studyIdentifier
            //            investigation
            //    | None -> 
            //        if verbosity >= 1 then printfn "Study with the identifier %s does not exist in the investigation file" studyIdentifier
            //        investigation
            //| None -> 
            //    if verbosity >= 1 then printfn "The investigation does not contain any studies"  
            //    investigation
            //|> Investigation.toFile investigationFilePath

        /// Gets an existing person by fullname (lastName, firstName, MidInitials) and prints their metadata.
        let get (arcConfiguration:ArcConfiguration) (personArgs : Map<string,Argument>) =

            printfn "Not implemented yet."
          
            //let verbosity = GeneralConfiguration.getVerbosity arcConfiguration
            
            //if verbosity >= 1 then printfn "Start Person Get"

            //let lastName = (getFieldValueByName  "LastName"   personArgs)
            //let firstName = (getFieldValueByName  "FirstName"     personArgs)
            //let midInitials = (getFieldValueByName  "MidInitials"  personArgs)

            //let studyIdentifier = getFieldValueByName "StudyIdentifier" personArgs

            //let investigationFilePath = IsaModelConfiguration.tryGetInvestigationFilePath arcConfiguration |> Option.get
            
            //let investigation = Investigation.fromFile investigationFilePath

            //// TO DO: Implementation für Assay-Datenstruktur

            //match investigation.Studies with
            //| Some studies -> 
            //    match API.Study.tryGetByIdentifier studyIdentifier studies with
            //    | Some study -> 
            //        match study.Contacts with
            //        | Some persons -> 
            //            match API.Person.tryGetByFullName firstName midInitials lastName persons with
            //            | Some person ->
            //                [person]
            //                |> Prompt.serializeXSLXWriterOutput (Contacts.writePersons None)
            //                |> printfn "%s"
            //            | None -> printfn "Person with the name %s %s %s  does not exist in the study with the identifier %s" firstName midInitials lastName studyIdentifier
            //        | None -> 
            //            if verbosity >= 1 then printfn "The study with the identifier %s does not contain any persons" studyIdentifier
            //    | None -> 
            //        if verbosity >= 1 then printfn "Study with the identifier %s does not exist in the investigation file" studyIdentifier
            //| None -> 
            //    if verbosity >= 1 then printfn "The investigation does not contain any studies"


        /// Lists the full names of all persons included in the investigation.
        let list (arcConfiguration : ArcConfiguration) = 

            printfn "Not implemented yet."

            //let verbosity = GeneralConfiguration.getVerbosity arcConfiguration
            
            //if verbosity >= 1 then printfn "Start Person List"

            //let investigationFilePath = IsaModelConfiguration.tryGetInvestigationFilePath arcConfiguration |> Option.get
            
            //let investigation = Investigation.fromFile investigationFilePath

            //// TO DO: Implementation für Assay-Datenstruktur

            //match investigation.Studies with
            //| Some studies -> 
            //    studies
            //    |> Seq.iter (fun study ->
            //        match study.Contacts with
            //        | Some persons -> 
                   
            //            printfn "Study: %s" (Option.defaultValue "" study.Identifier)
            //            persons 
            //            |> Seq.iter (fun person -> 
            //                let firstName = Option.defaultValue "" person.FirstName
            //                let midInitials = Option.defaultValue "" person.MidInitials
            //                let lastName = Option.defaultValue "" person.LastName
            //                if midInitials = "" then
            //                    printfn "--Person: %s %s" firstName lastName
            //                else
            //                    printfn "--Person: %s %s %s" firstName midInitials lastName)
            //        | None -> ()
            //    )
            //| None -> 
            //    if verbosity >= 1 then printfn "The investigation does not contain any studies"  