namespace ArcCommander.APIs

open ArcCommander
open ArcCommander.ArgumentProcessing

open System
open System.IO
open ISADotNet
open ISADotNet.XLSX
open FSharpSpreadsheetML

/// ArcCommander Study API functions that get executed by the study focused subcommand verbs.
module StudyAPI =
    
    /// API for working with study folders.
    module StudyFolder =
        
        /// Checks if an study folder exists in the ARC.
        let exists (arcConfiguration : ArcConfiguration) (identifier : string) =
            StudyConfiguration.getFolderPath identifier arcConfiguration
            |> System.IO.Directory.Exists

    module StudyFile =
    
        let exists (arcConfiguration : ArcConfiguration) (identifier : string) =

            let log = Logging.createLogger "StudyFileExistsLog"

            log.Trace "Start StudyFile.exists"

            let studyFilePath = IsaModelConfiguration.getStudyFilePath identifier arcConfiguration

            log.Trace "Check for file existence"

            let fileExists = File.Exists studyFilePath

            log.Trace "Check for folder existence"

            let folderExists = Directory.GetParent(studyFilePath).FullName |> Directory.Exists

            match fileExists, folderExists with
            | true, _ -> true
            | false, true ->
                log.Trace "Study file cannot be found in the study's folder."
                false
            | _ ->
                log.Trace "Study file and folder can not be found."
                false
    

        let create (arcConfiguration : ArcConfiguration) (study : Study) (studyIdentifier : string) =

            let log = Logging.createLogger "StudyFileCreateLog"

            log.Trace "Start StudyFile.create"

            let studyFilePath = IsaModelConfiguration.getStudyFilePath studyIdentifier arcConfiguration

            let study = {study with FileName = Some (IsaModelConfiguration.getStudyFileName studyIdentifier arcConfiguration)}

            log.Trace "Create study directory and file"
            
            if StudyFolder.exists arcConfiguration studyIdentifier then
                log.Error($"Study folder with identifier {studyIdentifier} already exists.")
            else
                StudyConfiguration.getSubFolderPaths studyIdentifier arcConfiguration
                |> Array.iter (
                    Directory.CreateDirectory 
                    >> fun dir -> File.Create(Path.Combine(dir.FullName, ".gitkeep")) |> ignore 
                )

                StudyFile.Study.init (Some study) studyIdentifier studyFilePath

                StudyConfiguration.getFilePaths studyIdentifier arcConfiguration
                |> Array.iter (File.Create >> ignore)
          

    /// Initializes a new empty study file in the ARC.
    let init (arcConfiguration : ArcConfiguration) (studyArgs : Map<string,Argument>) = 
            
        let log = Logging.createLogger "StudyInitLog"
        
        log.Info("Start Study Init")

        let identifier = getFieldValueByName "Identifier" studyArgs

        let study = 
            let studyInfo = 
                Study.StudyInfo.create
                    (identifier)
                    (getFieldValueByName "Title"                studyArgs)
                    (getFieldValueByName "Description"          studyArgs)
                    (getFieldValueByName "SubmissionDate"       studyArgs)
                    (getFieldValueByName "PublicReleaseDate"    studyArgs)
                    (IsaModelConfiguration.getStudyFileName identifier arcConfiguration)
                    []
            Study.fromParts studyInfo [] [] [] [] [] [] 

        if StudyFile.exists arcConfiguration identifier then
            log.Error("Study file already exists.")
        else 
            StudyFile.create arcConfiguration study identifier

    /// Updates an existing study info in the ARC with the given study metadata contained in cliArgs.
    let update (arcConfiguration : ArcConfiguration) (studyArgs : Map<string,Argument>) =
    
        let log = Logging.createLogger "StudyUpdateLog"
        
        log.Info("Start Study Update")

        // ?TODO? <- Test this : Add updateoption which updates by existing values and appends list
        let updateOption = if containsFlag "ReplaceWithEmptyValues" studyArgs then API.Update.UpdateAllAppendLists else API.Update.UpdateByExisting

        let studyIdentifier = getFieldValueByName "Identifier" studyArgs

        let newStudy = 
            let studyInfo = 
                Study.StudyInfo.create
                    (studyIdentifier)
                    (getFieldValueByName "Title"                studyArgs)
                    (getFieldValueByName "Description"          studyArgs)
                    (getFieldValueByName "SubmissionDate"       studyArgs)
                    (getFieldValueByName "PublicReleaseDate"    studyArgs)
                    (IsaModelConfiguration.getStudyFileName studyIdentifier arcConfiguration)
                    []
            Study.fromParts studyInfo [] [] [] [] [] [] 

        let studyFilepath = IsaModelConfiguration.getStudyFilePath studyIdentifier arcConfiguration
        
        let oldStudyFile = Spreadsheet.fromFile studyFilepath true

        log.Info "Writing into Study file"
        
        // update study file
        try StudyFile.MetaData.overwriteWithStudyInfo "Study" newStudy oldStudyFile

        finally Spreadsheet.close oldStudyFile

        let investigationFilePath = IsaModelConfiguration.tryGetInvestigationFilePath arcConfiguration |> Option.get
       
        let investigation = Investigation.fromFile investigationFilePath

        log.Info "Writing into Investigation file"

        // update investigation file
        match investigation.Studies with
        | Some studies -> 
            if API.Study.existsByIdentifier studyIdentifier studies then
                API.Study.updateByIdentifier updateOption newStudy studies
                |> API.Investigation.setStudies investigation
            else 
                let msg = $"Study with the identifier {studyIdentifier} does not exist in the investigation."
                if containsFlag "AddIfMissing" studyArgs then 
                    log.Warn msg
                    log.Info("Registering study as AddIfMissing Flag was set.")
                    API.Study.add studies newStudy
                    |> API.Investigation.setStudies investigation
                else 
                    log.Error msg
                    log.Info("AddIfMissing argument can be used to register study with the update command if it is missing.")
                    investigation
        | None -> 
            let msg = "The investigation does not contain any studies."
            if containsFlag "AddIfMissing" studyArgs then 
                log.Warn msg
                log.Info("Registering study as AddIfMissing Flag was set.")
                [newStudy]
                |> API.Investigation.setStudies investigation
            else 
                log.Error msg
                log.Trace("AddIfMissing argument can be used to register study with the update command if it is missing.")
                investigation
        |> Investigation.toFile investigationFilePath
        

    // /// Opens an existing study file in the ARC with the text editor set in globalArgs, additionally setting the given study metadata contained in cliArgs.
    /// Opens the existing study info in the ARC with the text editor set in globalArgs.
    let edit (arcConfiguration : ArcConfiguration) (studyArgs : Map<string,Argument>) = 

        let log = Logging.createLogger "StudyEditLog"
        
        log.Info("Start Study Edit")

        let studyIdentifier = getFieldValueByName "Identifier" studyArgs

        let editor = GeneralConfiguration.getEditor arcConfiguration

        let studyFilepath = IsaModelConfiguration.getStudyFilePath studyIdentifier arcConfiguration

        let oldStudy = StudyFile.Study.fromFile studyFilepath

        let oldStudyFile = Spreadsheet.fromFile studyFilepath true

        let editedStudy =
            ArgumentProcessing.Prompt.createIsaItemQuery editor Study.StudyInfo.toRows 
                (Study.StudyInfo.fromRows 1 >> fun (_,_,_,item) -> Study.fromParts item [] [] [] [] [] []) 
                oldStudy

        log.Info "Writing into Study file"
        
        // edit study file
        try StudyFile.MetaData.overwriteWithStudyInfo "Study" editedStudy oldStudyFile

        finally Spreadsheet.close oldStudyFile

        let investigationFilePath = IsaModelConfiguration.tryGetInvestigationFilePath arcConfiguration |> Option.get
       
        let investigation = Investigation.fromFile investigationFilePath

        let updateOption = if containsFlag "ReplaceWithEmptyValues" studyArgs then API.Update.UpdateAllAppendLists else API.Update.UpdateByExisting

        log.Info "Writing into Investigation file"

        // edit investigation file
        match investigation.Studies with
        | Some studies -> 
            if API.Study.existsByIdentifier studyIdentifier studies then
                API.Study.updateByIdentifier updateOption editedStudy studies
                |> API.Investigation.setStudies investigation
            else 
                let msg = $"Study with the identifier {studyIdentifier} does not exist in the investigation."
                if containsFlag "AddIfMissing" studyArgs then 
                    log.Warn msg
                    log.Info("Registering study as AddIfMissing Flag was set.")
                    API.Study.add studies editedStudy
                    |> API.Investigation.setStudies investigation
                else 
                    log.Error msg
                    log.Info("AddIfMissing argument can be used to register study with the update command if it is missing.")
                    investigation
        | None -> 
            let msg = "The investigation does not contain any studies."
            if containsFlag "AddIfMissing" studyArgs then 
                log.Warn msg
                log.Info("Registering study as AddIfMissing Flag was set.")
                [editedStudy]
                |> API.Investigation.setStudies investigation
            else 
                log.Error msg
                log.Trace("AddIfMissing argument can be used to register study with the update command if it is missing.")
                investigation
        |> Investigation.toFile investigationFilePath

    /// Registers an existing study in the ARC's investigation file with the given study metadata contained in cliArgs.
    let register (arcConfiguration : ArcConfiguration) (studyArgs : Map<string,Argument>) =

        let log = Logging.createLogger "StudyRegisterLog"
        
        log.Info("Start Study Register")

        let identifier = getFieldValueByName "Identifier" studyArgs

        let study = 
            let studyInfo = 
                Study.StudyInfo.create
                    identifier
                    (getFieldValueByName "Title"                studyArgs)
                    (getFieldValueByName "Description"          studyArgs)
                    (getFieldValueByName "SubmissionDate"       studyArgs)
                    (getFieldValueByName "PublicReleaseDate"    studyArgs)
                    (IsaModelConfiguration.getStudyFileName identifier arcConfiguration)
                    []
            Study.fromParts studyInfo [] [] [] [] [] [] 

        let investigationFilePath = IsaModelConfiguration.tryGetInvestigationFilePath arcConfiguration |> Option.get
        
        let investigation = Investigation.fromFile investigationFilePath

        match investigation.Studies with
        | None -> [study]
        | Some studies -> 
            match API.Study.tryGetByIdentifier identifier studies with
            | None -> API.Study.add studies study
            | Some _ -> 
                log.Error($"Study with the identifier {identifier} already exists in the investigation file.")
                studies
        |> API.Investigation.setStudies investigation
        |> Investigation.toFile investigationFilePath

    /// Creates a new study file in the ARC and registers it in the ARC's investigation file with the given study metadata contained in cliArgs.
    let add (arcConfiguration : ArcConfiguration) (studyArgs : Map<string,Argument>) = 
        init arcConfiguration studyArgs
        register arcConfiguration studyArgs

    /// Deletes a study's folder and underlying file structure from the ARC.
    let delete (arcConfiguration : ArcConfiguration) (studyArgs : Map<string,Argument>) = 
    
        let log = Logging.createLogger "StudyDeleteLog"
        
        log.Info("Start Study Delete")

        let isForced = (containsFlag "Force" studyArgs)

        let identifier = getFieldValueByName "Identifier" studyArgs

        let studyFolderPath = StudyConfiguration.getFolderPath identifier arcConfiguration

        /// Standard files that should always be present in a study.
        let standard = [|
            IsaModelConfiguration.getStudyFilePath identifier arcConfiguration 
            |> Path.truncateFolderPath identifier
            yield!
                StudyConfiguration.getFilePaths identifier arcConfiguration 
                |> Array.map (Path.truncateFolderPath identifier)
            yield!
                StudyConfiguration.getSubFolderPaths identifier arcConfiguration
                |> Array.map (
                    fun p -> Path.Combine(p, ".gitkeep")
                    >> Path.truncateFolderPath identifier
                )
        |]

        /// Actual files found.
        let allFiles =
            Directory.GetFiles(studyFolderPath, "*", SearchOption.AllDirectories)
            |> Array.map (Path.truncateFolderPath identifier)
        
        /// A check if there are no files in the folder that are not standard.
        let isStandard = Array.forall (fun t -> Array.contains t standard) allFiles

        match isForced, isStandard with
        | true, _
        | false, true ->
            try Directory.Delete(studyFolderPath, true) with
            | err -> log.Error($"Cannot delete study:\n {err.ToString()}")
        | _ ->
            log.Error "Study contains user-specific files. Deletion aborted."
            log.Info "Run the command with `--force` to force deletion."
            

    /// Unregisters an existing study from the ARC's investigation file.
    let unregister (arcConfiguration : ArcConfiguration) (studyArgs : Map<string,Argument>) =

        let log = Logging.createLogger "StudyUnregisterLog"
        
        log.Info("Start Study Unregister")

        let identifier = getFieldValueByName "Identifier" studyArgs

        let investigationFilePath = IsaModelConfiguration.tryGetInvestigationFilePath arcConfiguration |> Option.get
        
        let investigation = Investigation.fromFile investigationFilePath

        match investigation.Studies with
        | None -> 
            log.Error("The investigation does not contain any studies.")
            investigation
        | Some studies -> 
            match API.Study.tryGetByIdentifier identifier studies with
            | None -> 
                log.Error($"Study with the identifier {identifier} does not exist in the investigation file.")
                investigation
            | Some study -> 
                match study.Assays with
                | None | Some [] -> ()
                | Some assays -> 
                    log.Warn($"Study with the identifier {identifier} still contained following assays which might remain unregistered when the study is removed: ")
                    assays 
                    |> List.iter (fun a -> 
                        let identifier = 
                            a.FileName 
                             |> Option.bind (fun fn -> IsaModelConfiguration.tryGetAssayIdentifierOfFileName fn arcConfiguration)
                             |> Option.get
                        log.Warn($"Assay \"{identifier}\"")
                    )
                    log.Info($"You can register the assays to a different study using \"arc a register\"")
                API.Study.removeByIdentifier identifier studies 
                |> API.Investigation.setStudies investigation
        |> Investigation.toFile investigationFilePath

    /// Removes a study file from the ARC and unregisters it from the investigation file.
    let remove (arcConfiguration : ArcConfiguration) (studyArgs : Map<string,Argument>) = 
        delete arcConfiguration studyArgs
        unregister arcConfiguration studyArgs

    /// Lists all study identifiers registered in this ARC's investigation file.
    let show (arcConfiguration : ArcConfiguration) (studyArgs : Map<string,Argument>) =

        let log = Logging.createLogger "StudyShowLog"
        
        log.Info("Start Study Show")

        let identifier = getFieldValueByName "Identifier" studyArgs

        let investigationFilePath = IsaModelConfiguration.tryGetInvestigationFilePath arcConfiguration |> Option.get  

        let investigation = Investigation.fromFile investigationFilePath
        
        match investigation.Studies with
        | None -> log.Error("The investigation does not contain any studies.")
        | Some studies -> 
            match API.Study.tryGetByIdentifier identifier studies with
            | Some study ->
                study
                |> Prompt.serializeXSLXWriterOutput Study.StudyInfo.toRows
                |> log.Debug
            | None -> 
                log.Error($"Study with the identifier {identifier} does not exist in the investigation file.")
                ()
            

    /// Lists all study identifiers registered in this ARC's investigation file.
    let list (arcConfiguration : ArcConfiguration) =
        
        let log = Logging.createLogger "StudyListLog"
        
        log.Info("Start Study List")

        let investigationFilePath = IsaModelConfiguration.tryGetInvestigationFilePath arcConfiguration |> Option.get  
        log.Debug($"InvestigationFile: {investigationFilePath}")

        let investigation = Investigation.fromFile investigationFilePath
        
        let studyFileIdentifiers = set (IsaModelConfiguration.findStudyIdentifiers arcConfiguration)

        let studyIdentifiers = 
            investigation.Studies
            |> Option.defaultValue []
            |> List.choose (fun s -> 
                match s.Identifier with
                | None | Some "" -> 
                    log.Warn("Study does not have identifier")
                    None
                | Some i -> Some i
            ) 
            |> set
            
        let onlyRegistered = Set.difference studyIdentifiers studyFileIdentifiers
        let onlyInitialized = Set.difference studyFileIdentifiers studyIdentifiers
        let combined = Set.union studyIdentifiers studyFileIdentifiers

        if not onlyRegistered.IsEmpty then
            log.Warn("The ARC contains following registered studies that have no associated file:")
            onlyRegistered
            |> Seq.iter ((sprintf "%s") >> log.Warn) 
            log.Info($"You can init the study file using \"arc s init\"")

        if not onlyInitialized.IsEmpty then
            log.Warn("The ARC contains study files with the following identifiers not registered in the investigation:")
            onlyInitialized
            |> Seq.iter ((sprintf "%s") >> log.Warn) 
            log.Info($"You can register the study using \"arc s register\"")

        if combined.IsEmpty then
            log.Error("The ARC does not contain any studies.")

        combined
        |> Seq.iter (fun identifier ->
            log.Debug(sprintf "Study: %s" identifier)
        )

    /// Functions for altering investigation contacts.
    module Contacts =

        /// Updates an existing person in the ARC investigation study with the given person metadata contained in cliArgs.
        let update (arcConfiguration : ArcConfiguration) (personArgs : Map<string,Argument>) =

            let log = Logging.createLogger "StudyContactsUpdateLog"

            log.Info("Start Person Update")

            let updateOption = if containsFlag "ReplaceWithEmptyValues" personArgs then API.Update.UpdateAll else API.Update.UpdateByExisting            

            let lastName    = getFieldValueByName "LastName"    personArgs
            let firstName   = getFieldValueByName "FirstName"   personArgs
            let midInitials = getFieldValueByName "MidInitials" personArgs

            let comments = 
                match tryGetFieldValueByName "ORCID" personArgs with
                | Some orcid -> [Comment.fromString "Investigation Person ORCID" orcid]
                | None -> []

            let person = 
                Contacts.fromString
                    lastName
                    firstName
                    midInitials
                    (getFieldValueByName  "Email"                       personArgs)
                    (getFieldValueByName  "Phone"                       personArgs)
                    (getFieldValueByName  "Fax"                         personArgs)
                    (getFieldValueByName  "Address"                     personArgs)
                    (getFieldValueByName  "Affiliation"                 personArgs)
                    (getFieldValueByName  "Roles"                       personArgs)
                    (getFieldValueByName  "RolesTermAccessionNumber"    personArgs)
                    (getFieldValueByName  "RolesTermSourceREF"          personArgs)
                    comments

            let investigationFilePath = IsaModelConfiguration.tryGetInvestigationFilePath arcConfiguration |> Option.get
            
            let investigation = Investigation.fromFile investigationFilePath

            let studyIdentifier = getFieldValueByName "StudyIdentifier" personArgs

            let studyFilepath = IsaModelConfiguration.getStudyFilePath studyIdentifier arcConfiguration

            let oldStudy = StudyFile.Study.fromFile studyFilepath

            log.Info "Writing into Study file"

            // write into Study file
            match oldStudy.Contacts with
            | None -> 
                let msg = $"The study with the identifier {studyIdentifier} does not contain any persons."
                if containsFlag "AddIfMissing" personArgs then
                    log.Warn($"{msg}")
                    log.Info("Registering person as AddIfMissing Flag was set.")
                    let oldStudyFile = Spreadsheet.fromFile studyFilepath true
                    try StudyFile.MetaData.overwriteWithStudyInfo "Study" ([person] |> API.Study.setContacts oldStudy) oldStudyFile
                    finally Spreadsheet.close oldStudyFile
                else 
                    log.Error($"{msg}")
                    log.Trace("AddIfMissing argument can be used to register person with the update command if it is missing.")
            | Some persons ->
                if API.Person.existsByFullName firstName midInitials lastName persons then
                    let newStudy = 
                        API.Person.updateByFullName updateOption person persons
                        |> API.Study.setContacts oldStudy
                    let oldStudyFile = Spreadsheet.fromFile studyFilepath true
                    try StudyFile.MetaData.overwriteWithStudyInfo "Study" newStudy oldStudyFile
                    finally Spreadsheet.close oldStudyFile
                else 
                    let msg = $"Person with the name {firstName} {midInitials} {lastName} does not exist in the study with the identifier {studyIdentifier}."
                    if containsFlag "AddIfMissing" personArgs then
                        log.Warn($"{msg}")
                        log.Info("Registering person as AddIfMissing Flag was set.")
                        let oldStudyFile = Spreadsheet.fromFile studyFilepath true
                        try StudyFile.MetaData.overwriteWithStudyInfo "Study" (API.Person.add persons person |> API.Study.setContacts oldStudy) oldStudyFile
                        finally Spreadsheet.close oldStudyFile
                    else 
                        log.Error($"{msg}")
                        log.Trace("AddIfMissing argument can be used to register person with the update command if it is missing.")
            
            log.Info "Writing into Investigation file"
            
            // write into Investigation file
            match investigation.Studies with
            | None -> 
                log.Error("The investigation does not contain any studies.")
                investigation
            | Some studies -> 
                match API.Study.tryGetByIdentifier studyIdentifier studies with
                | None -> 
                    log.Error($"Study with the identifier {studyIdentifier} does not exist in the investigation file.")
                    investigation
                | Some study -> 
                    match study.Contacts with
                    | None -> 
                        let msg = $"The study with the identifier {studyIdentifier} does not contain any persons."
                        if containsFlag "AddIfMissing" personArgs then
                            log.Warn($"{msg}")
                            log.Info("Registering person as AddIfMissing Flag was set.")
                            [person]
                            |> API.Study.setContacts study
                        else 
                            log.Error($"{msg}")
                            log.Trace("AddIfMissing argument can be used to register person with the update command if it is missing.")
                            study
                    | Some persons -> 
                        if API.Person.existsByFullName firstName midInitials lastName persons then
                            API.Person.updateByFullName updateOption person persons
                            |> API.Study.setContacts study
                        else
                            let msg = $"Person with the name {firstName} {midInitials} {lastName} does not exist in the study with the identifier {studyIdentifier}."
                            if containsFlag "AddIfMissing" personArgs then
                                log.Warn($"{msg}")
                                log.Info("Registering person as AddIfMissing Flag was set.")
                                API.Person.add persons person
                                |> API.Study.setContacts study
                            else 
                                log.Error($"{msg}")
                                log.Trace("AddIfMissing argument can be used to register person with the update command if it is missing.")
                                study
                    |> fun s -> API.Study.updateByIdentifier API.Update.UpdateAll s studies
                    |> API.Investigation.setStudies investigation
            |> Investigation.toFile investigationFilePath
        
        /// Opens an existing person by fullname (LastName, FirstName, MidInitials) in the ARC investigation study with the text editor set in globalArgs.
        let edit (arcConfiguration : ArcConfiguration) (personArgs : Map<string,Argument>) =

            let log = Logging.createLogger "StudyContactsEdit"
            
            log.Info("Start Person Edit")

            let editor = GeneralConfiguration.getEditor arcConfiguration

            let lastName    = (getFieldValueByName "LastName"       personArgs)
            let firstName   = (getFieldValueByName "FirstName"      personArgs)
            let midInitials = (getFieldValueByName "MidInitials"    personArgs)

            let studyIdentifier = getFieldValueByName "StudyIdentifier" personArgs

            let investigationFilePath = IsaModelConfiguration.tryGetInvestigationFilePath arcConfiguration |> Option.get
            
            let investigation = Investigation.fromFile investigationFilePath

            let studyFilepath = IsaModelConfiguration.getStudyFilePath studyIdentifier arcConfiguration

            let oldStudy = StudyFile.Study.fromFile studyFilepath

            log.Info "Writing into Study file"

            // define new study and write into Study file
            let oldStudyPerson, newStudy =
                match oldStudy.Contacts with
                | None -> 
                    log.Error($"The study with the identifier {studyIdentifier} does not contain any persons.")
                    None, oldStudy
                | Some persons ->
                    match API.Person.tryGetByFullName firstName midInitials lastName persons with
                    | None -> 
                        log.Error($"Person with the name {firstName} {midInitials} {lastName} does not exist in the study with the identifier {studyIdentifier}.")
                        None, oldStudy
                    | Some person ->
                        let newStudy = 
                            ArgumentProcessing.Prompt.createIsaItemQuery editor
                                (List.singleton >> Contacts.toRows None) 
                                (Contacts.fromRows None 1 >> fun (_,_,_,items) -> items.Head) 
                                person
                            |> fun p -> API.Person.updateBy ((=) person) API.Update.UpdateAll p persons
                            |> API.Study.setContacts oldStudy
                        let oldStudyFile = Spreadsheet.fromFile studyFilepath true
                        try StudyFile.MetaData.overwriteWithStudyInfo "Study" newStudy oldStudyFile
                        finally Spreadsheet.close oldStudyFile
                        Some person, newStudy

            log.Info "Writing into Investigation file"
            
            // write into Investigation file
            match investigation.Studies with
            | None -> 
                log.Error("The investigation does not contain any studies.")
                investigation
            | Some studies -> 
                match API.Study.tryGetByIdentifier studyIdentifier studies with
                | None -> 
                    log.Error($"Study with the identifier {studyIdentifier} does not exist in the investigation file.")
                    investigation
                | Some study -> 
                    match study.Contacts with
                    | None -> 
                        log.Error($"The study with the identifier {studyIdentifier} does not contain any persons.")
                        investigation
                    | Some persons -> 
                        match API.Person.tryGetByFullName firstName midInitials lastName persons with
                        | None ->
                            log.Error($"Person with the name {firstName} {midInitials} {lastName} does not exist in the study with the identifier {studyIdentifier}.")
                            investigation
                        | Some person -> 
                            match oldStudyPerson with
                            | None -> ()
                            | Some osp -> if osp <> person then log.Warn $"Person {firstName} {midInitials} {lastName} differs between Investigation file and Study file."
                            API.Study.updateByIdentifier API.Update.UpdateAll newStudy studies
                            |> API.Investigation.setStudies investigation
            |> Investigation.toFile investigationFilePath

        /// Registers a person in the ARC investigation study with the given person metadata contained in personArgs.
        let register (arcConfiguration : ArcConfiguration) (personArgs : Map<string,Argument>) =

            let log = Logging.createLogger "StudyContactsRegisterLog"
            
            log.Info("Start Person Register")

            let lastName    = getFieldValueByName "LastName"    personArgs
            let firstName   = getFieldValueByName "FirstName"   personArgs
            let midInitials = getFieldValueByName "MidInitials" personArgs

            let comments = 
                match tryGetFieldValueByName "ORCID" personArgs with
                | Some orcid -> [Comment.fromString "Investigation Person ORCID" orcid]
                | None -> []

            let person = 
                Contacts.fromString
                    lastName
                    firstName
                    midInitials
                    (getFieldValueByName  "Email"                       personArgs)
                    (getFieldValueByName  "Phone"                       personArgs)
                    (getFieldValueByName  "Fax"                         personArgs)
                    (getFieldValueByName  "Address"                     personArgs)
                    (getFieldValueByName  "Affiliation"                 personArgs)
                    (getFieldValueByName  "Roles"                       personArgs)
                    (getFieldValueByName  "RolesTermAccessionNumber"    personArgs)
                    (getFieldValueByName  "RolesTermSourceREF"          personArgs)
                    comments
            
            let studyIdentifier = getFieldValueByName "StudyIdentifier" personArgs

            let investigationFilePath = IsaModelConfiguration.tryGetInvestigationFilePath arcConfiguration |> Option.get
            
            let investigation = Investigation.fromFile investigationFilePath

            let studyFilepath = IsaModelConfiguration.getStudyFilePath studyIdentifier arcConfiguration

            let oldStudy = StudyFile.Study.fromFile studyFilepath

            log.Info "Writing into Study file"

            // write into Study file
            match oldStudy.Contacts with
            | None -> 
                let oldStudyFile = Spreadsheet.fromFile studyFilepath true
                try StudyFile.MetaData.overwriteWithStudyInfo "Study" ([person] |> API.Study.setContacts oldStudy) oldStudyFile
                finally Spreadsheet.close oldStudyFile
            | Some persons ->
                if API.Person.existsByFullName firstName midInitials lastName persons then log.Info($"Person with the name {firstName} {midInitials} {lastName} already exists in the Study file.")
                else 
                    let oldStudyFile = Spreadsheet.fromFile studyFilepath true
                    try StudyFile.MetaData.overwriteWithStudyInfo "Study" (API.Person.add persons person |> API.Study.setContacts oldStudy) oldStudyFile
                    finally Spreadsheet.close oldStudyFile

            log.Info "Writing into Investigation file"
            
            // write into Investigation file
            match investigation.Studies with
            | None -> 
                log.Error("The investigation does not contain any studies.")
                investigation
            | Some studies -> 
                match API.Study.tryGetByIdentifier studyIdentifier studies with
                | None ->
                    log.Error($"Study with the identifier {studyIdentifier} does not exist in the investigation file.")
                    investigation
                | Some study -> 
                    match study.Contacts with
                    | None -> [person]
                    | Some persons -> 
                        if API.Person.existsByFullName firstName midInitials lastName persons then
                            log.Info($"Person with the name {firstName} {midInitials} {lastName} already exists in the investigation file.")
                            persons
                        else
                            API.Person.add persons person
                    |> API.Study.setContacts study
                    |> fun s -> API.Study.updateByIdentifier API.Update.UpdateAll s studies
                    |> API.Investigation.setStudies investigation
            |> Investigation.toFile investigationFilePath
    

        /// Removes an existing person by fullname (LastName, FirstName, MidInitials) from the ARC with the text editor set in globalArgs.
        let unregister (arcConfiguration : ArcConfiguration) (personArgs : Map<string,Argument>) =

            let log = Logging.createLogger "StudyContactsUnregister"
            
            log.Info("Start Person Unregister")

            let lastName    = (getFieldValueByName "LastName"       personArgs)
            let firstName   = (getFieldValueByName "FirstName"      personArgs)
            let midInitials = (getFieldValueByName "MidInitials"    personArgs)

            let studyIdentifier = getFieldValueByName "StudyIdentifier" personArgs

            let investigationFilePath = IsaModelConfiguration.tryGetInvestigationFilePath arcConfiguration |> Option.get
            
            let investigation = Investigation.fromFile investigationFilePath

            let studyFilepath = IsaModelConfiguration.getStudyFilePath studyIdentifier arcConfiguration

            let oldStudy = StudyFile.Study.fromFile studyFilepath

            log.Info "Writing into Study file"

            // write into Study file
            match oldStudy.Contacts with
            | Some persons ->
                if API.Person.existsByFullName firstName midInitials lastName persons then
                    let oldStudyFile = Spreadsheet.fromFile studyFilepath true
                    let newStudy =
                        API.Person.removeByFullName firstName midInitials lastName persons
                        |> API.Study.setContacts oldStudy
                    try StudyFile.MetaData.overwriteWithStudyInfo "Study" newStudy oldStudyFile
                    finally Spreadsheet.close oldStudyFile
                else log.Error($"Person with the name {firstName} {midInitials} {lastName} does not exist in the study with the identifier {studyIdentifier}.")
            | None -> log.Error $"The study with the identifier {studyIdentifier} does not contain any persons."

            log.Info "Writing into Investigation file"

            // write into Investigation file
            match investigation.Studies with
            | None -> 
                log.Error("The investigation does not contain any studies.")
                investigation
            | Some studies -> 
                match API.Study.tryGetByIdentifier studyIdentifier studies with
                | None -> 
                    log.Error($"Study with the identifier {studyIdentifier} does not exist in the investigation file.")
                    investigation
                | Some study -> 
                    match study.Contacts with
                    | None -> 
                        log.Error($"The study with the identifier {studyIdentifier} does not contain any persons.")
                        investigation
                    | Some persons -> 
                        if API.Person.existsByFullName firstName midInitials lastName persons then
                            API.Person.removeByFullName firstName midInitials lastName persons
                            |> API.Study.setContacts study
                            |> fun s -> API.Study.updateByIdentifier API.Update.UpdateAll s studies
                            |> API.Investigation.setStudies investigation
                        else
                            log.Error($"Person with the name {firstName} {midInitials} {lastName} does not exist in the study with the identifier {studyIdentifier}.")
                            investigation
            |> Investigation.toFile investigationFilePath

        /// Gets an existing person by fullname (LastName, FirstName, MidInitials) and prints its metadata.
        let show (arcConfiguration : ArcConfiguration) (personArgs : Map<string,Argument>) =
          
            let log = Logging.createLogger "StudyContactsShowLog"
            
            log.Info("Start Person Show")

            let lastName    = (getFieldValueByName "LastName"       personArgs)
            let firstName   = (getFieldValueByName "FirstName"      personArgs)
            let midInitials = (getFieldValueByName "MidInitials"    personArgs)

            let investigationFilePath = IsaModelConfiguration.tryGetInvestigationFilePath arcConfiguration |> Option.get
            
            let investigation = Investigation.fromFile investigationFilePath

            let studyIdentifier = getFieldValueByName "StudyIdentifier" personArgs

            log.Info "Checking Investigation file"

            let personOfInvFile = 
                match investigation.Studies with
                | None -> 
                    log.Error("The investigation does not contain any studies.")
                    None
                | Some studies -> 
                    match API.Study.tryGetByIdentifier studyIdentifier studies with
                    | None -> 
                        log.Error($"Study with the identifier {studyIdentifier} does not exist in the investigation file.")
                        None
                    | Some study -> 
                        match study.Contacts with
                        | None -> 
                            log.Error($"The study with the identifier {studyIdentifier} does not contain any persons.")
                            None
                        | Some persons -> 
                            match API.Person.tryGetByFullName firstName midInitials lastName persons with
                            | Some contact -> Some contact
                            | None -> 
                                log.Error $"Person with the name {firstName} {midInitials} {lastName} does not exist in the study with the identifier {studyIdentifier}."
                                None

            let studyFilepath = IsaModelConfiguration.getStudyFilePath studyIdentifier arcConfiguration

            log.Info "Checking Study file"

            let study = StudyFile.Study.fromFile studyFilepath

            match study.Contacts with
            | Some contacts ->
                match API.Person.tryGetByFullName firstName midInitials lastName contacts with
                | Some contact -> 
                    let printStudyPerson () =
                        contact
                        |> List.singleton
                        |> Prompt.serializeXSLXWriterOutput (Contacts.toRows None)
                        |> log.Debug
                    match personOfInvFile with
                    | Some poif ->
                        if contact <> poif then 
                            log.Warn $"Person with the name {firstName} {midInitials} {lastName} differs between Investigation and Study file."
                            let poifSer = 
                                List.singleton personOfInvFile.Value
                                |> Prompt.serializeXSLXWriterOutput (Contacts.toRows None)
                            log.Debug $"Person in Investigation file:\n{poifSer}\n\nPerson in Study file:"
                            printStudyPerson ()
                    | None -> printStudyPerson ()
                | None ->
                    log.Error $"Person with the name {firstName} {midInitials} {lastName} does not exist in the study with the identifier {studyIdentifier}."
            | None -> log.Error $"The study with the identifier {studyIdentifier} does not contain any persons."


        /// Lists the full names of all persons included in the investigation.
        let list (arcConfiguration : ArcConfiguration) = 

            let log = Logging.createLogger "StudyContactsListLog"
            
            log.Info("Start Person List")

            let investigationFilePath = IsaModelConfiguration.tryGetInvestigationFilePath arcConfiguration |> Option.get
            
            let investigation = Investigation.fromFile investigationFilePath

            let investigationPersons =
                match investigation.Studies with
                | Some studies -> 
                    studies
                    |> Seq.map (fun study -> study.Identifier, study.Contacts)
                    |> Some
                | None -> 
                    log.Error("The investigation does not contain any studies.")
                    None

            let printSc sc =
                sc
                |> Seq.iter (fun person -> 
                    let firstName = Option.defaultValue "" person.FirstName
                    let midInitials = Option.defaultValue "" person.MidInitials
                    let lastName = Option.defaultValue "" person.LastName
                    if midInitials = "" then log.Debug($"--Person: {firstName} {lastName}")
                    else log.Debug($"--Person: {firstName} {midInitials} {lastName}")
                )

            match investigationPersons with
            | None -> ()
            | Some invPers ->
                invPers
                |> Seq.iter (
                    fun (studyID,studyContacts) ->
                        match studyID with
                        | None -> ()
                        | Some sid ->
                            let studyFilepath = IsaModelConfiguration.getStudyFilePath sid arcConfiguration
                            let study = StudyFile.Study.fromFile studyFilepath
                            match study.Contacts, studyContacts with
                            | None, None -> log.Info $"Study {sid} does not contain any persons."
                            | None, Some isc ->
                                log.Warn $"Persons in Study {sid} are not present in Study file but in Investigation file"
                                log.Debug "Persons in Investigation file:"
                                printSc isc
                            | Some ssc, None ->
                                log.Warn $"Persons in Study {sid} are not present in Investigation file but in Study file"
                                log.Debug "Persons in Study file:"
                                printSc ssc
                            | Some ssc, Some isc ->
                                // not optimal: if the names are the same but some not printed information is different, you won't see this in the console output
                                if ssc <> isc then 
                                    log.Warn $"Persons in Study {sid} differ between Study file and Investigation file."
                                    log.Debug "Persons in Investigation file:"
                                    printSc isc
                                    log.Debug "\nPersons in Study file:"
                                    printSc ssc
                                else printSc ssc
                )

    /// Functions for altering investigation Publications.
    module Publications =

        /// Updates an existing publication in the ARC investigation study with the given publication metadata contained in cliArgs.
        let update (arcConfiguration : ArcConfiguration) (publicationArgs : Map<string,Argument>) =

            let log = Logging.createLogger "StudyPublicationsUpdateLog"
            
            log.Info("Start Publication update")

            let updateOption = if containsFlag "ReplaceWithEmptyValues" publicationArgs then API.Update.UpdateAll else API.Update.UpdateByExisting

            let doi = getFieldValueByName  "DOI"                        publicationArgs

            let publication =
                 Publications.fromString
                     (getFieldValueByName  "PubMedID"                   publicationArgs)
                     doi
                     (getFieldValueByName  "AuthorList"                 publicationArgs)
                     (getFieldValueByName  "Title"                      publicationArgs)
                     (getFieldValueByName  "Status"                     publicationArgs)
                     (getFieldValueByName  "StatusTermAccessionNumber"  publicationArgs)
                     (getFieldValueByName  "StatusTermSourceREF"        publicationArgs)
                     []

            let investigationFilePath = IsaModelConfiguration.tryGetInvestigationFilePath arcConfiguration |> Option.get
            
            let investigation = Investigation.fromFile investigationFilePath

            let studyIdentifier = getFieldValueByName "StudyIdentifier" publicationArgs

            log.Info "Write into Investigation file"

            // write into Investigation file
            match investigation.Studies with
            | None -> 
                log.Error("The investigation does not contain any studies.")
                investigation
            | Some studies -> 
                match API.Study.tryGetByIdentifier studyIdentifier studies with
                | None -> 
                    log.Error($"Study with the identifier {studyIdentifier} does not exist in the investigation file.")
                    investigation
                | Some study -> 
                    match study.Publications with
                    | Some publications -> 
                        if API.Publication.existsByDoi doi publications then
                            API.Publication.updateByDOI updateOption publication publications
                            |> API.Study.setPublications study
                        else
                            let msg = $"Publication with the DOI {doi} does not exist in the study with the identifier {studyIdentifier}."
                            if containsFlag "AddIfMissing" publicationArgs then
                                log.Warn($"{msg}")
                                log.Info("Registering publication as AddIfMissing Flag was set.")
                                API.Publication.add publications publication
                                |> API.Study.setPublications study
                            else 
                                log.Error($"{msg}")
                                log.Trace("AddIfMissing argument can be used to register publication with the update command if it is missing.")
                                study
                    | None -> 
                        let msg = $"The study with the identifier {studyIdentifier} does not contain any publications."
                        if containsFlag "AddIfMissing" publicationArgs then
                            log.Warn($"{msg}")
                            log.Info("Registering publication as AddIfMissing Flag was set.")
                            [publication]
                            |> API.Study.setPublications study
                        else 
                            log.Error($"{msg}")
                            log.Trace("AddIfMissing argument can be used to register publication with the update command if it is missing.")
                            study
                    |> fun s -> API.Study.updateByIdentifier API.Update.UpdateAll s studies
                    |> API.Investigation.setStudies investigation
            |> Investigation.toFile investigationFilePath

            let studyFilepath = IsaModelConfiguration.getStudyFilePath studyIdentifier arcConfiguration

            let oldStudy = StudyFile.Study.fromFile studyFilepath

            log.Info "Write into Study file"

            // write into Study file
            match oldStudy.Publications with
            | None -> 
                let msg = $"The study with the identifier {studyIdentifier} does not contain any publications."
                if containsFlag "AddIfMissing" publicationArgs then
                    log.Warn($"{msg}")
                    log.Info("Registering publication as AddIfMissing Flag was set.")
                    let oldStudyFile = Spreadsheet.fromFile studyFilepath true
                    try StudyFile.MetaData.overwriteWithStudyInfo "Study" ([publication] |> API.Study.setPublications oldStudy) oldStudyFile
                    finally Spreadsheet.close oldStudyFile
                else 
                    log.Error($"{msg}")
                    log.Trace("AddIfMissing argument can be used to register publication with the update command if it is missing.")
            | Some publications ->
                if API.Publication.existsByDoi doi publications then
                    let newStudy = 
                        API.Publication.updateByDOI updateOption publication publications
                        |> API.Study.setPublications oldStudy
                    let oldStudyFile = Spreadsheet.fromFile studyFilepath true
                    try StudyFile.MetaData.overwriteWithStudyInfo "Study" newStudy oldStudyFile
                    finally Spreadsheet.close oldStudyFile
                else 
                    let msg = $"Publication with the DOI {doi} does not exist in the study with the identifier {studyIdentifier}."
                    if containsFlag "AddIfMissing" publicationArgs then
                        log.Warn($"{msg}")
                        log.Info("Registering publication as AddIfMissing Flag was set.")
                        let oldStudyFile = Spreadsheet.fromFile studyFilepath true
                        try StudyFile.MetaData.overwriteWithStudyInfo "Study" (API.Publication.add publications publication |> API.Study.setPublications oldStudy) oldStudyFile
                        finally Spreadsheet.close oldStudyFile
                    else 
                        log.Error($"{msg}")
                        log.Trace("AddIfMissing argument can be used to register publication with the update command if it is missing.")
            
        
        /// Opens an existing publication by DOI in the ARC investigation study with the text editor set in globalArgs.
        let edit (arcConfiguration : ArcConfiguration) (publicationArgs : Map<string,Argument>) =

            let log = Logging.createLogger "StudyPublicationsEditLog"
            
            log.Info("Start Publication Edit")

            let editor = GeneralConfiguration.getEditor arcConfiguration

            let doi = (getFieldValueByName "DOI" publicationArgs)

            let studyIdentifier = getFieldValueByName "StudyIdentifier" publicationArgs

            let investigationFilePath = IsaModelConfiguration.tryGetInvestigationFilePath arcConfiguration |> Option.get
            
            let investigation = Investigation.fromFile investigationFilePath

            let studyFilepath = IsaModelConfiguration.getStudyFilePath studyIdentifier arcConfiguration

            let oldStudy = StudyFile.Study.fromFile studyFilepath

            log.Info "Write into study file"

            // define new study and write into Study file
            let oldStudyPublication, newStudy =
                match oldStudy.Publications with
                | None -> 
                    log.Error($"The study with the identifier {studyIdentifier} does not contain any publications.")
                    None, oldStudy
                | Some publications ->
                    match API.Publication.tryGetByDoi doi publications with
                    | None -> 
                        log.Error($"Publication with the DOI {doi} does not exist in the Study with the identifier {studyIdentifier}.")
                        None, oldStudy
                    | Some publication ->
                        let newStudy = 
                            ArgumentProcessing.Prompt.createIsaItemQuery editor
                                (List.singleton >> Publications.toRows None) 
                                (Publications.fromRows None 1 >> fun (_,_,_,items) -> items.Head) 
                                publication
                            |> fun p -> API.Publication.updateBy ((=) publication) API.Update.UpdateAll p publications
                            |> API.Study.setPublications oldStudy
                        let oldStudyFile = Spreadsheet.fromFile studyFilepath true
                        try StudyFile.MetaData.overwriteWithStudyInfo "Study" newStudy oldStudyFile
                        finally Spreadsheet.close oldStudyFile
                        Some publication, newStudy
            
            log.Info "Write into Investigation file"

            // write into Investigation file
            match investigation.Studies with
            | None -> 
                log.Error("The investigation does not contain any studies.")
                investigation
            | Some studies -> 
                match API.Study.tryGetByIdentifier studyIdentifier studies with
                | None -> 
                    log.Error($"Study with the identifier {studyIdentifier} does not exist in the investigation file.")
                    investigation
                | Some study -> 
                    match study.Publications with
                    | None -> 
                        log.Error($"The study with the identifier {studyIdentifier} does not contain any publications.")
                        investigation
                    | Some publications -> 
                        // TODO : Remove the "Some" when the
                        match API.Publication.tryGetByDoi doi publications with
                        | None ->
                            log.Error($"Publication with the DOI {doi} does not exist in the study with the identifier {studyIdentifier}.")
                            investigation
                        | Some publication ->
                            match oldStudyPublication with
                            | None -> ()
                            | Some osp -> if osp <> publication then log.Warn $"Publication with the DOI {doi} differs between Investigation file and Study file"
                            API.Study.updateByIdentifier API.Update.UpdateAll newStudy studies
                            |> API.Investigation.setStudies investigation
            |> Investigation.toFile investigationFilePath


        /// Registers a publication in the ARC investigation study with the given publication metadata contained in publicationArgs.
        let register (arcConfiguration : ArcConfiguration) (publicationArgs : Map<string,Argument>) =

            let log = Logging.createLogger "StudyPublicationsRegisterLog"
            
            log.Info("Start Publication Register")

            let doi = getFieldValueByName  "DOI"                        publicationArgs

            let publication =
                 Publications.fromString
                     (getFieldValueByName  "PubMedID"                   publicationArgs)
                     doi
                     (getFieldValueByName  "AuthorList"                 publicationArgs)
                     (getFieldValueByName  "Title"                      publicationArgs)
                     (getFieldValueByName  "Status"                     publicationArgs)
                     (getFieldValueByName  "StatusTermAccessionNumber"  publicationArgs)
                     (getFieldValueByName  "StatusTermSourceREF"        publicationArgs)
                     []
            
            let studyIdentifier = getFieldValueByName "StudyIdentifier" publicationArgs

            let investigationFilePath = IsaModelConfiguration.tryGetInvestigationFilePath arcConfiguration |> Option.get
            
            let investigation = Investigation.fromFile investigationFilePath

            log.Info "Write into Investigation file"

            // write into Investigation file
            match investigation.Studies with
            | None -> 
                log.Error("The investigation does not contain any studies.")
                investigation
            | Some studies -> 
                match API.Study.tryGetByIdentifier studyIdentifier studies with
                | None ->
                    log.Error($"Study with the identifier {studyIdentifier} does not exist in the investigation file.")
                    investigation
                | Some study -> 
                    match study.Publications with
                    | None -> [publication]
                    | Some publications -> 
                        if API.Publication.existsByDoi doi publications then
                            log.Error($"Publication with the DOI {doi} already exists in the study with the identifier {studyIdentifier}.")
                            publications
                        else API.Publication.add publications publication
                    |> API.Study.setPublications study
                    |> fun s -> API.Study.updateByIdentifier API.Update.UpdateAll s studies
                    |> API.Investigation.setStudies investigation
            |> Investigation.toFile investigationFilePath

            let studyFilepath = IsaModelConfiguration.getStudyFilePath studyIdentifier arcConfiguration

            let oldStudy = StudyFile.Study.fromFile studyFilepath

            log.Info "Write into Study file"

            // write into Study file
            match oldStudy.Publications with
            | None -> 
                let oldStudyFile = Spreadsheet.fromFile studyFilepath true
                try StudyFile.MetaData.overwriteWithStudyInfo "Study" ([publication] |> API.Study.setPublications oldStudy) oldStudyFile
                finally Spreadsheet.close oldStudyFile
            | Some publications ->
                let newStudy = 
                    if API.Publication.existsByDoi doi publications then
                        log.Error $"Publication with the DOI {doi} already exists in the Study with the identifier {studyIdentifier}."
                        oldStudy
                    else
                        API.Publication.add publications publication
                        |> API.Study.setPublications oldStudy
                let oldStudyFile = Spreadsheet.fromFile studyFilepath true
                try StudyFile.MetaData.overwriteWithStudyInfo "Study" newStudy oldStudyFile
                finally Spreadsheet.close oldStudyFile
                

        /// Opens an existing publication by DOI in the ARC investigation study with the text editor set in globalArgs.
        let unregister (arcConfiguration : ArcConfiguration) (publicationArgs : Map<string,Argument>) =

            let log = Logging.createLogger "StudyPublicationsUnregisterLog"
            
            log.Info("Start Publication Unregister")

            let doi = (getFieldValueByName  "DOI"   publicationArgs)

            let studyIdentifier = getFieldValueByName "StudyIdentifier" publicationArgs

            let investigationFilePath = IsaModelConfiguration.tryGetInvestigationFilePath arcConfiguration |> Option.get
            
            let investigation = Investigation.fromFile investigationFilePath
            
            log.Info "Write into Investigation file"

            match investigation.Studies with
            | None -> 
                log.Error("The investigation does not contain any studies.")
                investigation
            | Some studies -> 
                match API.Study.tryGetByIdentifier studyIdentifier studies with
                | None ->
                    log.Error($"Study with the identifier {studyIdentifier} does not exist in the investigation file.")
                    investigation
                | Some study -> 
                    match study.Publications with
                    | None -> 
                        log.Error($"The study with the identifier {studyIdentifier} does not contain any publications.")
                        investigation
                    | Some publications -> 
                        if API.Publication.existsByDoi doi publications then
                            API.Publication.removeByDoi doi publications
                            |> API.Study.setPublications study
                            |> fun s -> API.Study.updateByIdentifier API.Update.UpdateAll s studies
                            |> API.Investigation.setStudies investigation
                        else
                            log.Error($"Publication with the DOI {doi} does not exist in the study with the identifier {studyIdentifier}.")
                            investigation
            |> Investigation.toFile investigationFilePath

            let studyFilepath = IsaModelConfiguration.getStudyFilePath studyIdentifier arcConfiguration

            let oldStudy = StudyFile.Study.fromFile studyFilepath

            log.Info "Write into Study file"

            // write into Study file
            match oldStudy.Publications with
            | None -> log.Error($"The study with the identifier {studyIdentifier} does not contain any publications.")
            | Some publications ->
                let newStudy = 
                    if API.Publication.existsByDoi doi publications then
                        API.Publication.removeByDoi doi publications
                        |> API.Study.setPublications oldStudy
                    else
                        log.Error($"Publication with the DOI {doi} does not exist in the study with the identifier {studyIdentifier}.")
                        oldStudy
                let oldStudyFile = Spreadsheet.fromFile studyFilepath true
                try StudyFile.MetaData.overwriteWithStudyInfo "Study" newStudy oldStudyFile
                finally Spreadsheet.close oldStudyFile

        /// Gets an existing publication by DOI from the ARC investigation study and prints its metadata.
        let show (arcConfiguration : ArcConfiguration) (publicationArgs : Map<string,Argument>) =

            let log = Logging.createLogger "StudyPublicationsShow"
            
            log.Info("Start Publication Show")

            let doi = (getFieldValueByName "DOI" publicationArgs)

            let studyIdentifier = getFieldValueByName "StudyIdentifier" publicationArgs

            let investigationFilePath = IsaModelConfiguration.tryGetInvestigationFilePath arcConfiguration |> Option.get
            
            let investigation = Investigation.fromFile investigationFilePath

            log.Info "Checking Investigation file"

            let publicationOfInvFile = 
                match investigation.Studies with
                | None -> 
                    log.Error("The investigation does not contain any studies.")
                    None
                | Some studies -> 
                    match API.Study.tryGetByIdentifier studyIdentifier studies with
                    | None -> 
                        log.Error($"Study with the identifier {studyIdentifier} does not exist in the investigation file.")
                        None
                    | Some study -> 
                        match study.Publications with
                        | None -> 
                            log.Error($"The study with the identifier {studyIdentifier} does not contain any publications.")
                            None
                        | Some publications -> 
                            match API.Publication.tryGetByDoi doi publications with
                            | Some publication -> Some publication
                            | None -> 
                                log.Error $"Publication with the DOI {doi} does not exist in the study with the identifier {studyIdentifier}."
                                None

            let studyFilepath = IsaModelConfiguration.getStudyFilePath studyIdentifier arcConfiguration

            log.Info "Checking Study file"

            let study = StudyFile.Study.fromFile studyFilepath

            match study.Publications with
            | Some publications ->
                match API.Publication.tryGetByDoi doi publications with
                | Some publication -> 
                    let printStudyPublication () =
                        publication
                        |> List.singleton
                        |> Prompt.serializeXSLXWriterOutput (Publications.toRows None)
                        |> log.Debug
                    match publicationOfInvFile with
                    | Some poif ->
                        if publication <> poif then 
                            log.Warn $"Publication with the DOI {doi} differs between Investigation and Study file."
                            let poifSer = 
                                List.singleton publicationOfInvFile.Value
                                |> Prompt.serializeXSLXWriterOutput (Publications.toRows None)
                            log.Debug $"Publication in Investigation file:\n{poifSer}\n\nPublication in Study file:"
                        printStudyPublication ()
                    | None -> printStudyPublication ()
                | None ->
                    log.Error $"Publication with the name {doi} does not exist in the study with the identifier {studyIdentifier}."
            | None -> log.Error $"The study with the identifier {studyIdentifier} does not contain any publications."


        /// Lists the DOIs of all publications included in the investigation study.
        let list (arcConfiguration : ArcConfiguration) = 

            let log = Logging.createLogger "StudyPublicationsListLog"
            
            log.Info("Start Publication List")

            let investigationFilePath = IsaModelConfiguration.tryGetInvestigationFilePath arcConfiguration |> Option.get
            
            let investigation = Investigation.fromFile investigationFilePath

            let investigationPublications =
                match investigation.Studies with
                | Some studies -> 
                    studies
                    |> Seq.map (fun study -> study.Identifier, study.Publications)
                    |> Some
                | None -> 
                    log.Error("The investigation does not contain any studies.")
                    None

            let printSp sp = sp |> Seq.iter (fun publication -> log.Debug(sprintf "--Publication DOI: %s" (Option.defaultValue "" publication.DOI)))

            match investigationPublications with
            | None -> ()
            | Some invPubs ->
                invPubs
                |> Seq.iter (
                    fun (studyID,studyPublications) ->
                        match studyID with
                        | None -> ()
                        | Some sid ->
                            let studyFilepath = IsaModelConfiguration.getStudyFilePath sid arcConfiguration
                            let study = StudyFile.Study.fromFile studyFilepath
                            match study.Publications, studyPublications with
                            | None, None -> log.Info $"Study {sid} does not contain any publications."
                            | None, Some isp ->
                                log.Warn $"Publications in Study {sid} are not present in Study file but in Investigation file"
                                log.Debug "Publications in Investigation file:"
                                printSp isp
                            | Some ssc, None ->
                                log.Warn $"Publications in Study {sid} are not present in Investigation file but in Study file"
                                log.Debug "Publications in Study file:"
                                printSp ssc
                            | Some ssp, Some isp ->
                                // not optimal: if the DOIs are the same but some not printed information is different, you won't see this in the console output
                                if ssp <> isp then 
                                    log.Warn $"Publications in Study {sid} differ between Study file and Investigation file."
                                    log.Debug "Publications in Investigation file:"
                                    printSp isp
                                    log.Debug "\nPublications in Study file:"
                                    printSp ssp
                                else printSp ssp
                )


    /// Functions for altering investigation Designs.
    module Designs =

        /// Updates an existing design in the ARC investigation study with the given design metadata contained in cliArgs.
        let update (arcConfiguration : ArcConfiguration) (designArgs : Map<string,Argument>) =

            let log = Logging.createLogger "StudyDesugnsUpdateLog"
            
            log.Info("Start Design Update")

            let updateOption = if containsFlag "ReplaceWithEmptyValues" designArgs then API.Update.UpdateAll else API.Update.UpdateByExisting            

            let name = getFieldValueByName "DesignType" designArgs

            let design = 
                 DesignDescriptors.fromString
                     name
                     (getFieldValueByName  "TypeTermAccessionNumber"    designArgs)
                     (getFieldValueByName  "TypeTermSourceREF"          designArgs)

                     []

            let investigationFilePath = IsaModelConfiguration.tryGetInvestigationFilePath arcConfiguration |> Option.get
            
            let investigation = Investigation.fromFile investigationFilePath

            let studyIdentifier = getFieldValueByName "StudyIdentifier" designArgs

            match investigation.Studies with
            | Some studies -> 
                match API.Study.tryGetByIdentifier studyIdentifier studies with
                | Some study -> 
                    match study.StudyDesignDescriptors with
                    | Some designs -> 
                        if API.OntologyAnnotation.existsByName design.Name.Value designs then
                            API.OntologyAnnotation.updateByName updateOption design designs
                            |> API.Study.setDescriptors study
                        else
                            let msg = $"Design with the name {name} does not exist in the study with the identifier {studyIdentifier}."
                            if containsFlag "AddIfMissing" designArgs then
                                log.Warn($"{msg}")
                                log.Info("Registering design as AddIfMissing Flag was set.")
                                API.OntologyAnnotation.add designs design
                                |> API.Study.setDescriptors study
                            else 
                                log.Error($"{msg}")
                                log.Trace("AddIfMissing argument can be used to register design with the update command if it is missing.")
                                study
                    | None -> 
                        let msg = $"The study with the identifier {studyIdentifier} does not contain any design descriptors."
                        if containsFlag "AddIfMissing" designArgs then
                            log.Warn($"{msg}")
                            log.Info("Registering design as AddIfMissing Flag was set.")
                            [design]
                            |> API.Study.setDescriptors study
                        else 
                            log.Error($"{msg}")
                            log.Trace("AddIfMissing argument can be used to register design with the update command if it is missing.")
                            study
                    |> fun s -> API.Study.updateByIdentifier API.Update.UpdateAll s studies
                    |> API.Investigation.setStudies investigation
                | None -> 
                    log.Error($"Study with the identifier {studyIdentifier} does not exist in the investigation file.")
                    investigation
            | None -> 
                log.Error("The investigation does not contain any studies.")
                investigation
            |> Investigation.toFile investigationFilePath
        
        /// Opens an existing design by design type in the ARC investigation study with the text editor set in globalArgs.
        let edit (arcConfiguration : ArcConfiguration) (designArgs : Map<string,Argument>) =

            let log = Logging.createLogger "StudyDesignsEdit"
            
            log.Info("Start Design Edit")

            let editor = GeneralConfiguration.getEditor arcConfiguration

            let name = (getFieldValueByName "DesignType" designArgs)

            let studyIdentifier = getFieldValueByName "StudyIdentifier" designArgs

            let investigationFilePath = IsaModelConfiguration.tryGetInvestigationFilePath arcConfiguration |> Option.get
            
            let investigation = Investigation.fromFile investigationFilePath
            
            match investigation.Studies with
            | Some studies -> 
                match API.Study.tryGetByIdentifier studyIdentifier studies with
                | Some study -> 
                    match study.StudyDesignDescriptors with
                    | Some designs -> 
                        match API.OntologyAnnotation.tryGetByName (AnnotationValue.fromString name) designs with
                        | Some design ->
                            ArgumentProcessing.Prompt.createIsaItemQuery editor
                                (List.singleton >> DesignDescriptors.toRows None) 
                                (DesignDescriptors.fromRows None 1 >> fun (_,_,_,items) -> items.Head) 
                                design
                            |> fun d -> API.OntologyAnnotation.updateBy ((=) design) API.Update.UpdateAll d designs
                            |> API.Study.setDescriptors study
                            |> fun s -> API.Study.updateByIdentifier API.Update.UpdateAll s studies
                            |> API.Investigation.setStudies investigation
                        | None ->
                            log.Error($"Design with the name {name} does not exist in the study with the identifier {studyIdentifier}.")
                            investigation
                    | None -> 
                        log.Error($"The study with the identifier {studyIdentifier} does not contain any design descriptors.")
                        investigation
                | None -> 
                    log.Error($"Study with the identifier {studyIdentifier} does not exist in the investigation file.")
                    investigation
            | None -> 
                log.Error("The investigation does not contain any studies.")
                investigation
            |> Investigation.toFile investigationFilePath


        /// Registers a design in the ARC investigation study with the given publication metadata contained in personArgs.
        let register (arcConfiguration : ArcConfiguration) (designArgs : Map<string,Argument>) =

            let log = Logging.createLogger "StudyDesignsRegisterLog"
            
            log.Info("Start Design Register")

            let name = getFieldValueByName "DesignType" designArgs

            let design = 
                DesignDescriptors.fromString
                    name
                    (getFieldValueByName  "TypeTermAccessionNumber"    designArgs)
                    (getFieldValueByName  "TypeTermSourceREF"          designArgs)
                    []
            
            let studyIdentifier = getFieldValueByName "StudyIdentifier" designArgs

            let investigationFilePath = IsaModelConfiguration.tryGetInvestigationFilePath arcConfiguration |> Option.get
            
            let investigation = Investigation.fromFile investigationFilePath

            log.Info "Write into Investigation file"

            // write into Investigation file
            match investigation.Studies with
            | None -> 
                log.Error("The Investigation does not contain any Studies.")
                investigation
            | Some studies -> 
                match API.Study.tryGetByIdentifier studyIdentifier studies with
                | None ->
                    log.Error($"Study with the identifier {studyIdentifier} does not exist in the Investigation file.")
                    investigation
                | Some study -> 
                    match study.StudyDesignDescriptors with
                    | None -> [design]
                    | Some designs -> 
                        if API.OntologyAnnotation.existsByName (AnnotationValue.fromString name) designs then
                            log.Error($"Design with the name {name} already exists in the Study with the identifier {studyIdentifier}.")
                            designs
                        else API.OntologyAnnotation.add designs design
                    |> API.Study.setDescriptors study
                    |> fun s -> API.Study.updateByIdentifier API.Update.UpdateAll s studies
                    |> API.Investigation.setStudies investigation
            |> Investigation.toFile investigationFilePath

            let studyFilepath = IsaModelConfiguration.getStudyFilePath studyIdentifier arcConfiguration

            let oldStudy = StudyFile.Study.fromFile studyFilepath

            log.Info "Write into Study file"

            // write into Study file
            match oldStudy.StudyDesignDescriptors with
            | None -> 
                let oldStudyFile = Spreadsheet.fromFile studyFilepath true
                try StudyFile.MetaData.overwriteWithStudyInfo "Study" ([design] |> API.Study.setDescriptors oldStudy) oldStudyFile
                finally Spreadsheet.close oldStudyFile
            | Some designs ->
                let newStudy = 
                    if API.OntologyAnnotation.existsByName (AnnotationValue.fromString name) designs then 
                        log.Error $"Design with the name {name} already exists in the Study with the identifier {studyIdentifier}."
                        oldStudy
                    else 
                        API.OntologyAnnotation.add designs design
                        |> API.Study.setDescriptors oldStudy
                let oldStudyFile = Spreadsheet.fromFile studyFilepath true
                try StudyFile.MetaData.overwriteWithStudyInfo "Study" newStudy oldStudyFile
                finally Spreadsheet.close oldStudyFile


        /// Opens an existing design by design type in the ARC investigation study with the text editor set in globalArgs.
        let unregister (arcConfiguration : ArcConfiguration) (designArgs : Map<string,Argument>) =
            
            let log = Logging.createLogger "StudyDesignsUnregisterLog"
            
            log.Info("Start Design Unregister")

            let name = getFieldValueByName "DesignType" designArgs

            let studyIdentifier = getFieldValueByName "StudyIdentifier" designArgs

            let investigationFilePath = IsaModelConfiguration.tryGetInvestigationFilePath arcConfiguration |> Option.get
            
            let investigation = Investigation.fromFile investigationFilePath

            log.Info "Write into Investigation file"

            // write into Investigation file
            match investigation.Studies with
            | None -> 
                log.Error("The Investigation does not contain any studies.")
                investigation
            | Some studies -> 
                match API.Study.tryGetByIdentifier studyIdentifier studies with
                | None ->
                    log.Error($"Study with the identifier {studyIdentifier} does not exist in the Investigation file.")
                    investigation
                | Some study -> 
                    match study.StudyDesignDescriptors with
                    | None -> 
                        log.Error($"The Study with the identifier {studyIdentifier} does not contain any design descriptors.")
                        investigation
                    | Some designs -> 
                        if API.OntologyAnnotation.existsByName (AnnotationValue.fromString name) designs then
                            API.OntologyAnnotation.removeByName (AnnotationValue.fromString name) designs
                            |> API.Study.setDescriptors study
                            |> fun s -> API.Study.updateByIdentifier API.Update.UpdateAll s studies
                            |> API.Investigation.setStudies investigation
                        else
                            log.Error($"Design with the name {name} does not exist in the study with the identifier {studyIdentifier}.")
                            investigation
            |> Investigation.toFile investigationFilePath

            let studyFilepath = IsaModelConfiguration.getStudyFilePath studyIdentifier arcConfiguration

            let oldStudy = StudyFile.Study.fromFile studyFilepath

            log.Info "Write into Study file"

            // write into Study file
            match oldStudy.StudyDesignDescriptors with
            | None -> log.Error($"The Study with the identifier {studyIdentifier} does not contain any designs.")
            | Some designs ->
                let newStudy = 
                    if API.OntologyAnnotation.existsByName (AnnotationValue.fromString name) designs then
                        API.OntologyAnnotation.removeByName (AnnotationValue.fromString name) designs
                        |> API.Study.setDescriptors oldStudy
                    else
                        log.Error($"Design with the name {name} does not exist in the Study with the identifier {studyIdentifier}.")
                        oldStudy
                let oldStudyFile = Spreadsheet.fromFile studyFilepath true
                try StudyFile.MetaData.overwriteWithStudyInfo "Study" newStudy oldStudyFile
                finally Spreadsheet.close oldStudyFile

        /// Gets an existing design by design type from the ARC investigation study and prints its metadata.
        let show (arcConfiguration : ArcConfiguration) (designArgs : Map<string,Argument>) =

            let log = Logging.createLogger "StudyDesignsShowLog"
            
            log.Info("Start Design Show")

            let name = getFieldValueByName "DesignType" designArgs

            let studyIdentifier = getFieldValueByName "StudyIdentifier" designArgs

            let investigationFilePath = IsaModelConfiguration.tryGetInvestigationFilePath arcConfiguration |> Option.get
            
            let investigation = Investigation.fromFile investigationFilePath

            match investigation.Studies with
            | Some studies -> 
                match API.Study.tryGetByIdentifier studyIdentifier studies with
                | Some study -> 
                    match study.StudyDesignDescriptors with
                    | Some designs -> 
                        match API.OntologyAnnotation.tryGetByName (AnnotationValue.fromString name) designs with
                        | Some design ->
                            [design]
                            |> Prompt.serializeXSLXWriterOutput (DesignDescriptors.toRows None)
                            |> log.Debug
                        | None -> 
                            log.Error($"Design with the DOI {name} does not exist in the study with the identifier {studyIdentifier}.")
                    | None -> 
                        log.Error($"The study with the identifier {studyIdentifier} does not contain any design descriptors.")
                | None -> 
                    log.Error($"Study with the identifier {studyIdentifier} does not exist in the investigation file.")
            | None -> 
                log.Error("The investigation does not contain any studies.")
        
        /// Lists the designs included in the investigation study.
        let list (arcConfiguration : ArcConfiguration) = 

            let log = Logging.createLogger "StudyDesignsListLog"
            
            log.Info("Start Design List")

            let investigationFilePath = IsaModelConfiguration.tryGetInvestigationFilePath arcConfiguration |> Option.get
            
            let investigation = Investigation.fromFile investigationFilePath

            match investigation.Studies with
            | Some studies -> 
                studies
                |> Seq.iter (fun study ->
                    match study.StudyDesignDescriptors with
                    | Some designs -> 
                        log.Debug(sprintf "Study: %s" (Option.defaultValue "" study.Identifier))
                        designs
                        |> Seq.iter (fun design -> log.Debug(sprintf "--Design Type: %s" (design.Name |> Option.map AnnotationValue.toString |> Option.defaultValue "" )))
                    | None -> ()
                )
            | None -> 
                log.Error("The investigation does not contain any studies.")

    /// Functions for altering investigation factors.
    module Factors =

        /// Updates an existing factor in the ARC investigation study with the given factor metadata contained in cliArgs.
        let update (arcConfiguration : ArcConfiguration) (factorArgs : Map<string,Argument>) =

            let log = Logging.createLogger "StudyFactorsUpdateLog"
            
            log.Info("Start Factor Update")

            let updateOption = if containsFlag "ReplaceWithEmptyValues" factorArgs then API.Update.UpdateAll else API.Update.UpdateByExisting            

            let name = getFieldValueByName "Name" factorArgs

            let factor = 
                 Factors.fromString
                    name
                    (getFieldValueByName  "FactorType"                 factorArgs)
                    (getFieldValueByName  "TypeTermAccessionNumber"    factorArgs)
                    (getFieldValueByName  "TypeTermSourceREF"          factorArgs)
                     []

            let investigationFilePath = IsaModelConfiguration.tryGetInvestigationFilePath arcConfiguration |> Option.get
            
            let investigation = Investigation.fromFile investigationFilePath

            let studyIdentifier = getFieldValueByName "StudyIdentifier" factorArgs

            match investigation.Studies with
            | Some studies -> 
                match API.Study.tryGetByIdentifier studyIdentifier studies with
                | Some study -> 
                    match study.Factors with
                    | Some factors -> 
                        if API.Factor.existsByName name factors then
                            API.Factor.updateByName updateOption factor factors
                            |> API.Study.setFactors study
                        else
                            let msg = $"Factor with the name {name} does not exist in the study with the identifier {studyIdentifier}."
                            if containsFlag "AddIfMissing" factorArgs then
                                log.Warn($"{msg}")
                                log.Info("Registering factor as AddIfMissing Flag was set.")
                                API.Factor.add factors factor
                                |> API.Study.setFactors study
                            else 
                                log.Error($"{msg}")
                                log.Trace("AddIfMissing argument can be used to register factor with the update command if it is missing.")
                                study
                    | None -> 
                        let msg = $"The study with the identifier {studyIdentifier} does not contain any factors."
                        if containsFlag "AddIfMissing" factorArgs then
                            log.Warn($"{msg}")
                            log.Info("Registering factor as AddIfMissing Flag was set.")
                            [factor]
                            |> API.Study.setFactors study
                        else 
                            log.Error($"{msg}")
                            log.Trace("AddIfMissing argument can be used to register factor with the update command if it is missing.")
                            study
                    |> fun s -> API.Study.updateByIdentifier API.Update.UpdateAll s studies
                    |> API.Investigation.setStudies investigation
                | None -> 
                    log.Error($"Study with the identifier {studyIdentifier} does not exist in the investigation file.")
                    investigation
            | None -> 
                log.Error("The investigation does not contain any studies.")
                investigation
            |> Investigation.toFile investigationFilePath
        
        /// Opens an existing factor by name in the ARC investigation study with the text editor set in globalArgs.
        let edit (arcConfiguration : ArcConfiguration) (factorArgs : Map<string,Argument>) =
            
            let log = Logging.createLogger "StudyFactorsEditLog"
            
            log.Info("Start Factor Edit")

            let editor = GeneralConfiguration.getEditor arcConfiguration

            let name = getFieldValueByName "Name" factorArgs

            let studyIdentifier = getFieldValueByName "StudyIdentifier" factorArgs

            let investigationFilePath = IsaModelConfiguration.tryGetInvestigationFilePath arcConfiguration |> Option.get
            
            let investigation = Investigation.fromFile investigationFilePath
            
            match investigation.Studies with
            | Some studies -> 
                match API.Study.tryGetByIdentifier studyIdentifier studies with
                | Some study -> 
                    match study.Factors with
                    | Some factors -> 
                        match API.Factor.tryGetByName name factors with
                        | Some factor ->                    
                            ArgumentProcessing.Prompt.createIsaItemQuery editor
                                (List.singleton >> Factors.toRows None) 
                                (Factors.fromRows None 1 >> fun (_,_,_,items) -> items.Head) 
                                factor
                            |> fun f -> API.Factor.updateBy ((=) factor) API.Update.UpdateAll f factors
                            |> API.Study.setFactors study
                            |> fun s -> API.Study.updateByIdentifier API.Update.UpdateAll s studies
                            |> API.Investigation.setStudies investigation
                        | None ->
                            log.Error($"Factor with the name {name} does not exist in the study with the identifier {studyIdentifier}.")
                            investigation
                    | None -> 
                        log.Error($"The study with the identifier {studyIdentifier} does not contain any factors.")
                        investigation
                | None -> 
                    log.Error($"Study with the identifier {studyIdentifier} does not exist in the investigation file.")
                    investigation
            | None -> 
                log.Error("The investigation does not contain any studies.")
                investigation
            |> Investigation.toFile investigationFilePath


        /// Registers a factor in the ARC investigation study with the given factor metadata contained in personArgs.
        let register (arcConfiguration : ArcConfiguration) (factorArgs : Map<string,Argument>) =

            let log = Logging.createLogger "StudyFactorsRegisterLog"
            
            log.Info("Start Factor Register")
            
            let name = getFieldValueByName  "Name" factorArgs

            let factor = 
                 Factors.fromString
                    name
                    (getFieldValueByName  "FactorType"                 factorArgs)
                    (getFieldValueByName  "TypeTermAccessionNumber"    factorArgs)
                    (getFieldValueByName  "TypeTermSourceREF"          factorArgs)
                     []
            
            let studyIdentifier = getFieldValueByName "StudyIdentifier" factorArgs

            let investigationFilePath = IsaModelConfiguration.tryGetInvestigationFilePath arcConfiguration |> Option.get
            
            let investigation = Investigation.fromFile investigationFilePath

            match investigation.Studies with
            | Some studies -> 
                match API.Study.tryGetByIdentifier studyIdentifier studies with
                | Some study -> 
                    match study.Factors with
                    | Some factors -> 
                        if API.Factor.existsByName name factors then
                            log.Error($"Factor with the name {name} already exists in the study with the identifier {studyIdentifier}.")
                            factors
                        else
                            API.Factor.add factors factor
                    | None -> [factor]
                    |> API.Study.setFactors study
                    |> fun s -> API.Study.updateByIdentifier API.Update.UpdateAll s studies
                    |> API.Investigation.setStudies investigation
                | None ->
                    log.Error($"Study with the identifier {studyIdentifier} does not exist in the investigation file.")
                    investigation
            | None -> 
                log.Error("The investigation does not contain any studies.")
                investigation
            |> Investigation.toFile investigationFilePath

        /// Opens an existing factor by name in the ARC investigation study with the text editor set in globalArgs.
        let unregister (arcConfiguration : ArcConfiguration) (factorArgs : Map<string,Argument>) =

            let log = Logging.createLogger "StudyFactorsUnregisterLog"
            
            log.Info("Start Factor Unregister")
            
            let name = getFieldValueByName  "Name" factorArgs

            let studyIdentifier = getFieldValueByName "StudyIdentifier" factorArgs

            let investigationFilePath = IsaModelConfiguration.tryGetInvestigationFilePath arcConfiguration |> Option.get
            
            let investigation = Investigation.fromFile investigationFilePath

            match investigation.Studies with
            | Some studies -> 
                match API.Study.tryGetByIdentifier studyIdentifier studies with
                | Some study -> 
                    match study.Factors with
                    | Some factors -> 
                        if API.Factor.existsByName name factors then           
                            API.Factor.removeByName name factors
                            |> API.Study.setFactors study
                            |> fun s -> API.Study.updateByIdentifier API.Update.UpdateAll s studies
                            |> API.Investigation.setStudies investigation
                        else
                            log.Error($"Factor with the name {name} does not exist in the study with the identifier {studyIdentifier}.")
                            investigation
                    | None -> 
                        log.Error($"The study with the identifier {studyIdentifier} does not contain any factors.")
                        investigation
                | None ->
                    log.Error($"Study with the identifier {studyIdentifier} does not exist in the investigation file.")
                    investigation
            | None -> 
                log.Error("The investigation does not contain any studies.")
                investigation
            |> Investigation.toFile investigationFilePath

        /// Gets an existing factor by name from the ARC investigation study and prints its metadata.
        let show (arcConfiguration : ArcConfiguration) (factorArgs : Map<string,Argument>) =

            let log = Logging.createLogger "StudyFactorsShowLog"
            
            log.Info("Start Factor Show")

            let name = getFieldValueByName  "Name" factorArgs

            let studyIdentifier = getFieldValueByName "StudyIdentifier" factorArgs

            let investigationFilePath = IsaModelConfiguration.tryGetInvestigationFilePath arcConfiguration |> Option.get
            
            let investigation = Investigation.fromFile investigationFilePath

            match investigation.Studies with
            | Some studies -> 
                match API.Study.tryGetByIdentifier studyIdentifier studies with
                | Some study -> 
                    match study.Factors with
                    | Some factors -> 
                        match API.Factor.tryGetByName name factors with
                        | Some factor ->
                            [factor]
                            |> Prompt.serializeXSLXWriterOutput (Factors.toRows None)
                            |> log.Debug
                        | None -> 
                            log.Error($"Factor with the DOI {name} does not exist in the study with the identifier {studyIdentifier}.")
                    | None -> 
                        log.Error($"The study with the identifier {studyIdentifier} does not contain any factors.")
                | None -> 
                    log.Error($"Study with the identifier {studyIdentifier} does not exist in the investigation file.")
            | None -> 
                log.Error("The investigation does not contain any studies.")

        /// Lists the factors included in the investigation study.
        let list (arcConfiguration : ArcConfiguration) = 
            
            let log = Logging.createLogger "StudyFactorsListLog"
            
            log.Warn("Start Factor List")

            let investigationFilePath = IsaModelConfiguration.tryGetInvestigationFilePath arcConfiguration |> Option.get
            
            let investigation = Investigation.fromFile investigationFilePath

            match investigation.Studies with
            | Some studies -> 
                studies
                |> Seq.iter (fun study ->
                    match study.Factors with
                    | Some factors -> 
                        log.Debug(sprintf "Study: %s" (Option.defaultValue "" study.Identifier))
                        factors
                        |> Seq.iter (fun factor -> log.Debug(sprintf "--Factor Name: %s" (Option.defaultValue "" factor.Name)))
                    | None -> ()
                )
            | None -> 
                log.Error("The investigation does not contain any studies.")

    /// Functions for altering investigation protocols.
    module Protocols =

        /// Updates an existing protocol in the ARC investigation study with the given protocol metadata contained in cliArgs.
        let update (arcConfiguration : ArcConfiguration) (protocolArgs : Map<string,Argument>) =

            let log = Logging.createLogger "StudyProtocolsUpdateLog"
            
            log.Info("Start Protocol Update")

            let updateOption = if containsFlag "ReplaceWithEmptyValues" protocolArgs then API.Update.UpdateAll else API.Update.UpdateByExisting

            let name = getFieldValueByName "Name" protocolArgs

            let protocol = 
                 Protocols.fromString
                    name
                    (getFieldValueByName "ProtocolType"                         protocolArgs)
                    (getFieldValueByName "TypeTermAccessionNumber"              protocolArgs)
                    (getFieldValueByName "TypeTermSourceREF"                    protocolArgs)
                    (getFieldValueByName "Description"                          protocolArgs)
                    (getFieldValueByName "URI"                                  protocolArgs)
                    (getFieldValueByName "Version"                              protocolArgs)
                    (getFieldValueByName "ParametersName"                       protocolArgs)
                    (getFieldValueByName "ParametersTermAccessionNumber"        protocolArgs)
                    (getFieldValueByName "ParametersTermSourceREF"              protocolArgs)
                    (getFieldValueByName "ComponentsName"                       protocolArgs)
                    (getFieldValueByName "ComponentsType"                       protocolArgs)
                    (getFieldValueByName "ComponentsTypeTermAccessionNumber"    protocolArgs)
                    (getFieldValueByName "ComponentsTypeTermSourceREF"          protocolArgs)
                    []

            let investigationFilePath = IsaModelConfiguration.tryGetInvestigationFilePath arcConfiguration |> Option.get
            
            let investigation = Investigation.fromFile investigationFilePath

            let studyIdentifier = getFieldValueByName "StudyIdentifier" protocolArgs

            match investigation.Studies with
            | Some studies -> 
                match API.Study.tryGetByIdentifier studyIdentifier studies with
                | Some study -> 
                    match study.Protocols with
                    | Some protocols -> 
                        if API.Protocol.existsByName name protocols then
                            API.Protocol.updateByName updateOption protocol protocols
                            |> API.Study.setProtocols study
                        else
                            let msg = $"Protocol with the name {name} does not exist in the study with the identifier {studyIdentifier}."
                            if containsFlag "AddIfMissing" protocolArgs then
                                log.Warn($"{msg}")
                                log.Info("Registering protocol as AddIfMissing Flag was set.")
                                API.Protocol.add protocols protocol
                                |> API.Study.setProtocols study
                            else 
                                log.Error($"{msg}")
                                log.Trace("AddIfMissing argument can be used to register protocol with the update command if it is missing.")
                                study
                    | None -> 
                        let msg = $"The study with the identifier {studyIdentifier} does not contain any protocols."
                        if containsFlag "AddIfMissing" protocolArgs then
                            log.Warn($"{msg}")
                            log.Info("Registering protocol as AddIfMissing Flag was set.")
                            [protocol]
                            |> API.Study.setProtocols study
                        else 
                            log.Error($"{msg}")
                            log.Trace("AddIfMissing argument can be used to register protocol with the update command if it is missin.g")
                            study
                    |> fun s -> API.Study.updateByIdentifier API.Update.UpdateAll s studies
                    |> API.Investigation.setStudies investigation
                | None -> 
                    log.Error($"Study with the identifier {studyIdentifier} does not exist in the investigation file.")
                    investigation
            | None -> 
                log.Error("The investigation does not contain any studies.")
                investigation
            |> Investigation.toFile investigationFilePath
        
        /// Opens an existing protocol by name in the ARC investigation study with the text editor set in globalArgs.
        let edit (arcConfiguration : ArcConfiguration) (protocolArgs : Map<string,Argument>) =

            let log = Logging.createLogger "StudyProtocolsEditLog"
            
            log.Info("Start Protocol Edit")

            let editor = GeneralConfiguration.getEditor arcConfiguration

            let name = (getFieldValueByName  "Name" protocolArgs)

            let studyIdentifier = getFieldValueByName "StudyIdentifier" protocolArgs

            let investigationFilePath = IsaModelConfiguration.tryGetInvestigationFilePath arcConfiguration |> Option.get
            
            let investigation = Investigation.fromFile investigationFilePath
            
            match investigation.Studies with
            | Some studies -> 
                match API.Study.tryGetByIdentifier studyIdentifier studies with
                | Some study -> 
                    match study.Protocols with
                    | Some protocols -> 
                        match API.Protocol.tryGetByName name protocols with
                        | Some protocol ->
                            ArgumentProcessing.Prompt.createIsaItemQuery editor
                                (List.singleton >> Protocols.toRows None) 
                                (Protocols.fromRows None 1 >> fun (_,_,_,items) -> items.Head) 
                                protocol
                            |> fun f -> API.Protocol.updateBy ((=) protocol) API.Update.UpdateAll f protocols
                            |> API.Study.setProtocols study
                            |> fun s -> API.Study.updateByIdentifier API.Update.UpdateAll s studies
                            |> API.Investigation.setStudies investigation
                        | None ->
                            log.Error($"Protocol with the name {name} does not exist in the study with the identifier {studyIdentifier}.")
                            investigation
                    | None -> 
                        log.Error($"The study with the identifier {studyIdentifier} does not contain any protocols.")
                        investigation
                | None -> 
                    log.Error($"Study with the identifier {studyIdentifier} does not exist in the investigation file.")
                    investigation
            | None -> 
                log.Error("The investigation does not contain any studies.")
                investigation
            |> Investigation.toFile investigationFilePath


        /// Registers a protocol in the ARC investigation study with the given protocol metadata contained in personArgs.
        let register (arcConfiguration : ArcConfiguration) (protocolArgs : Map<string,Argument>) =
           
            let log = Logging.createLogger "StudyProtocolsRegisterLog"
            
            log.Info("Start Protocol Register")
            
            let name = getFieldValueByName "Name" protocolArgs

            let protocol = 
                 Protocols.fromString
                    name
                    (getFieldValueByName "ProtocolType"                         protocolArgs)
                    (getFieldValueByName "TypeTermAccessionNumber"              protocolArgs)
                    (getFieldValueByName "TypeTermSourceREF"                    protocolArgs)
                    (getFieldValueByName "Description"                          protocolArgs)
                    (getFieldValueByName "URI"                                  protocolArgs)
                    (getFieldValueByName "Version"                              protocolArgs)
                    (getFieldValueByName "ParametersName"                       protocolArgs)
                    (getFieldValueByName "ParametersTermAccessionNumber"        protocolArgs)
                    (getFieldValueByName "ParametersTermSourceREF"              protocolArgs)
                    (getFieldValueByName "ComponentsName"                       protocolArgs)
                    (getFieldValueByName "ComponentsType"                       protocolArgs)
                    (getFieldValueByName "ComponentsTypeTermAccessionNumber"    protocolArgs)
                    (getFieldValueByName "ComponentsTypeTermSourceREF"          protocolArgs)
                    []
            
            let studyIdentifier = getFieldValueByName "StudyIdentifier" protocolArgs

            let investigationFilePath = IsaModelConfiguration.tryGetInvestigationFilePath arcConfiguration |> Option.get
            
            let investigation = Investigation.fromFile investigationFilePath

            match investigation.Studies with
            | Some studies -> 
                match API.Study.tryGetByIdentifier studyIdentifier studies with
                | Some study -> 
                    match study.Protocols with
                    | Some protocols -> 
                        if API.Protocol.existsByName name protocols then
                            log.Error($"Protocol with the name {name} already exists in the study with the identifier {studyIdentifier}.")
                            protocols
                        else
                            API.Protocol.add protocols protocol
                    | None -> [protocol]
                    |> API.Study.setProtocols study
                    |> fun s -> API.Study.updateByIdentifier API.Update.UpdateAll s studies
                    |> API.Investigation.setStudies investigation
                | None ->
                    log.Error($"Study with the identifier {studyIdentifier} does not exist in the investigation file.")
                    investigation
            | None -> 
                log.Error($"The investigation does not contain any studies.")
                investigation
            |> Investigation.toFile investigationFilePath

        /// Opens an existing protocol by name in the ARC investigation study with the text editor set in globalArgs.
        let unregister (arcConfiguration : ArcConfiguration) (protocolArgs : Map<string,Argument>) =

            let log = Logging.createLogger "StudyProtocolsUnregisterLog"
            
            log.Info("Start Protocol Unregister")

            let name = getFieldValueByName "Name" protocolArgs

            let studyIdentifier = getFieldValueByName "StudyIdentifier" protocolArgs

            let investigationFilePath = IsaModelConfiguration.tryGetInvestigationFilePath arcConfiguration |> Option.get
            
            let investigation = Investigation.fromFile investigationFilePath

            match investigation.Studies with
            | Some studies -> 
                match API.Study.tryGetByIdentifier studyIdentifier studies with
                | Some study -> 
                    match study.Protocols with
                    | Some protocols -> 
                        if API.Protocol.existsByName name protocols then
                            API.Protocol.removeByName name protocols
                            |> API.Study.setProtocols study
                            |> fun s -> API.Study.updateByIdentifier API.Update.UpdateAll s studies
                            |> API.Investigation.setStudies investigation
                        else
                            log.Error($"Protocol with the name {name} does not exist in the study with the identifier {studyIdentifier}.")
                            investigation
                    | None -> 
                        log.Error($"The study with the identifier {studyIdentifier} does not contain any protocols.")
                        investigation
                | None ->
                    log.Error($"Study with the identifier {studyIdentifier} does not exist in the investigation file.")
                    investigation
            | None -> 
                log.Error("The investigation does not contain any studies.")
                investigation
            |> Investigation.toFile investigationFilePath

        /// Loads a protocol or process file from a given filepath and adds it to the study.
        let load (arcConfiguration : ArcConfiguration) (protocolArgs : Map<string,Argument>) =

            let log = Logging.createLogger "StudyProtocolsLoadLog"
            
            log.Info("Start Protocol Load")

            let editor = GeneralConfiguration.getEditor arcConfiguration

            let path = getFieldValueByName "InputPath" protocolArgs

            let protocol =
                if containsFlag "IsProcessFile" protocolArgs then
                    let isaProcess = Json.Process.fromFile path
                    isaProcess.ExecutesProtocol
                else
                    Json.Protocol.fromFile path |> Some
                |> Option.map (fun p -> 
                    if p.Name.IsNone then
                        log.Error("Given protocol does not contain a name, please add it in the editor.")
                        ArgumentProcessing.Prompt.createIsaItemQuery editor
                            (List.singleton >> Protocols.toRows None) 
                            (Protocols.fromRows None 1 >> fun (_,_,_,items) -> items.Head) 
                            p
                    else p
                )

            let studyIdentifier = getFieldValueByName "StudyIdentifier" protocolArgs

            let investigationFilePath = IsaModelConfiguration.tryGetInvestigationFilePath arcConfiguration |> Option.get
            
            let investigation = Investigation.fromFile investigationFilePath
           
            match investigation.Studies with
            | Some studies -> 
                match protocol with 
                | Some protocol ->
                    match API.Study.tryGetByIdentifier studyIdentifier studies with
                    | Some study -> 
                        let name = protocol.Name.Value
                        match study.Protocols with
                        | Some protocols ->
                            if API.Protocol.existsByName name protocols then
                                let msg = $"Protocol with the name {name} already exists in the study with the identifier {studyIdentifier}."
                                if containsFlag "UpdateExisting" protocolArgs then
                                    log.Warn($"{msg}")
                                    log.Info("Updating protocol as \"UpdateExisting\" flag was given.")
                                    API.Protocol.updateByName API.Update.UpdateAll protocol protocols
                                else
                                    log.Error($"{msg}")
                                    log.Info("Not updating protocol as \"UpdateExisting\" flag was not given.")
                                    protocols
                            else
                                log.Trace($"Protocol with the name {name} does not exist in the study with the identifier {studyIdentifier}.")
                                API.Protocol.add protocols protocol
                        | None -> [protocol]
                        |> API.Study.setProtocols study
                        |> fun s -> API.Study.updateByIdentifier API.Update.UpdateAll s studies
                        |> API.Investigation.setStudies investigation
                    | None ->
                        log.Error($"Study with the identifier {studyIdentifier} does not exist in the investigation file.")
                        investigation
                | None ->
                    log.Error("The process file did not contain a protocol.")
                    investigation
            | None -> 
                log.Error("The investigation does not contain any studies.")
                investigation
            |> Investigation.toFile investigationFilePath

        /// Gets an existing protocol by name from the ARC investigation study and prints its metadata.
        let show (arcConfiguration : ArcConfiguration) (protocolArgs : Map<string,Argument>) =
         
            let log = Logging.createLogger "StudyProtocolsShowLog"
            
            log.Info("Start Protocol Show")

            let name = getFieldValueByName "Name" protocolArgs

            let studyIdentifier = getFieldValueByName "StudyIdentifier" protocolArgs

            let investigationFilePath = IsaModelConfiguration.tryGetInvestigationFilePath arcConfiguration |> Option.get
            
            let investigation = Investigation.fromFile investigationFilePath

            match investigation.Studies with
            | Some studies -> 
                match API.Study.tryGetByIdentifier studyIdentifier studies with
                | Some study -> 
                    match study.Protocols with
                    | Some protocols -> 
                        match API.Protocol.tryGetByName name protocols with
                        | Some protocol ->
                            [protocol]
                            |> Prompt.serializeXSLXWriterOutput (Protocols.toRows None)
                            |> log.Debug
                        | None -> 
                            log.Error($"Protocol with the DOI {name} does not exist in the study with the identifier {studyIdentifier}.")
                    | None -> 
                        log.Error($"The study with the identifier {studyIdentifier} does not contain any protocols.")
                | None -> 
                    log.Error($"Study with the identifier {studyIdentifier} does not exist in the investigation file.")
            | None -> 
                log.Error("The investigation does not contain any studies.")
                

        /// Lists the protocols included in the investigation study.
        let list (arcConfiguration : ArcConfiguration) = 

            let log = Logging.createLogger "StudyProtocolsListLog"
            
            log.Info"Start Protocol List"

            let investigationFilePath = IsaModelConfiguration.tryGetInvestigationFilePath arcConfiguration |> Option.get
            
            let investigation = Investigation.fromFile investigationFilePath

            match investigation.Studies with
            | Some studies -> 
                studies
                |> Seq.iter (fun study ->
                    match study.Protocols with
                    | Some protocols -> 
                        log.Debug(sprintf "Study: %s" (Option.defaultValue "" study.Identifier))
                        protocols
                        |> Seq.iter (fun factor -> log.Debug(sprintf "--Protocol Name: %s" (Option.defaultValue "" factor.Name)))
                    | None -> ()
                )
            | None -> 
                log.Error("The investigation does not contain any studies.")