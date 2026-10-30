namespace OpmlSyncNewsBlur

open System
open System.Collections.Generic
open System.Net.Http
open System.Text.Json
open System.Threading

type NewsBlurClient(config: SyncConfig, client: HttpClient) =
    let loginLock = new SemaphoreSlim(1, 1)
    let mutable loggedIn = false

    let login () =
        task {
            use content =
                new FormUrlEncodedContent(
                    [ KeyValuePair("username", config.NewsBlurUsername)
                      KeyValuePair("password", config.NewsBlurPassword) ]
                )

            use! response = client.PostAsync("api/login", content)
            response.EnsureSuccessStatusCode() |> ignore
            let! body = response.Content.ReadAsStringAsync()
            use json = JsonDocument.Parse(body)
            let root = json.RootElement
            let mutable authenticated = Unchecked.defaultof<JsonElement>

            if root.TryGetProperty("authenticated", &authenticated)
               && authenticated.ValueKind = JsonValueKind.False then
                invalidOp "NewsBlur rejected the configured credentials."

            loggedIn <- true
        }

    let ensureLoggedIn () =
        task {
            if not loggedIn then
                do! loginLock.WaitAsync()

                try
                    if not loggedIn then
                        do! login ()
                finally
                    loginLock.Release() |> ignore
        }

    let postForm (path: string) (fields: KeyValuePair<string, string> list) =
        task {
            do! ensureLoggedIn ()
            use content = new FormUrlEncodedContent(fields)
            use! response = client.PostAsync(path, content)
            response.EnsureSuccessStatusCode() |> ignore
            return! response.Content.ReadAsStringAsync()
        }

    let parseApiCode (body: string) =
        use json = JsonDocument.Parse(body)
        let mutable code = Unchecked.defaultof<JsonElement>

        if json.RootElement.TryGetProperty("code", &code) then
            match code.ValueKind with
            | JsonValueKind.Number -> code.GetInt32()
            | JsonValueKind.String ->
                match Int32.TryParse(code.GetString()) with
                | true, value -> value
                | _ -> 0
            | _ -> 0
        else
            1

    let folderPaths (root: JsonElement) =
        let byFeed = Dictionary<int, string>()
        let folders = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        let mutable foldersElement = Unchecked.defaultof<JsonElement>
        let mutable flatFoldersElement = Unchecked.defaultof<JsonElement>

        let addFeedToFolder folder (item: JsonElement) =
            let mutable feedId = 0

            let parsed =
                if item.ValueKind = JsonValueKind.Number then
                    item.TryGetInt32(&feedId)
                elif item.ValueKind = JsonValueKind.String then
                    match Int32.TryParse(item.GetString()) with
                    | true, value -> feedId <- value; true
                    | _ -> false
                else
                    false

            if parsed then
                byFeed[feedId] <- folder

        let addFolderAncestors (folder: string) =
            let mutable parent = ""

            for part in folder.Split('/', StringSplitOptions.RemoveEmptyEntries) do
                parent <- if String.IsNullOrEmpty(parent) then part else parent + "/" + part
                folders.Add(parent) |> ignore

        let rec visit (path: string list) (element: JsonElement) =
            match element.ValueKind with
            | JsonValueKind.Object ->
                for property in element.EnumerateObject() do
                    match property.Value.ValueKind with
                    | JsonValueKind.Array ->
                        let folderPath = String.Join("/", path @ [ property.Name ])
                        addFolderAncestors folderPath

                        for item in property.Value.EnumerateArray() do
                            addFeedToFolder folderPath item
                    | JsonValueKind.Object ->
                        visit (path @ [ property.Name ]) property.Value
                    | _ ->
                        ()
            | _ ->
                ()

        if root.TryGetProperty("flat_folders", &flatFoldersElement)
           && flatFoldersElement.ValueKind = JsonValueKind.Object then
            for folder in flatFoldersElement.EnumerateObject() do
                addFolderAncestors folder.Name

                if folder.Value.ValueKind = JsonValueKind.Array then
                    for item in folder.Value.EnumerateArray() do
                        addFeedToFolder folder.Name item
        elif root.TryGetProperty("folders", &foldersElement) then
            visit [] foldersElement

        byFeed, Set.ofSeq folders

    let addFolderPath (folder: string) (knownFolders: Set<string>) =
        task {
            if String.IsNullOrWhiteSpace(folder) then
                return knownFolders
            else
                let parts = folder.Split('/', StringSplitOptions.RemoveEmptyEntries)
                let mutable known = knownFolders
                let mutable parent = ""

                for part in parts do
                    let current = if String.IsNullOrEmpty(parent) then part else parent + "/" + part

                    if not (known.Contains(current)) then
                        let! body =
                            postForm
                                "reader/add_folder"
                                [ KeyValuePair("folder", part)
                                  KeyValuePair("parent_folder", parent) ]

                        if parseApiCode body <= 0 then
                            invalidOp $"NewsBlur could not create folder '{current}'."

                        known <- known.Add(current)

                    parent <- current

                return known
        }

    member _.GetFeeds() =
        task {
            do! ensureLoggedIn ()
            use! response = client.GetAsync("reader/feeds?flat=true&include_favicons=false")
            response.EnsureSuccessStatusCode() |> ignore
            let! body = response.Content.ReadAsStringAsync()
            use json = JsonDocument.Parse(body)
            let root = json.RootElement
            let foldersByFeed, folders = folderPaths root
            let mutable feedsElement = Unchecked.defaultof<JsonElement>

            if not (root.TryGetProperty("feeds", &feedsElement))
               || feedsElement.ValueKind <> JsonValueKind.Object then
                invalidOp "NewsBlur returned a feed list with an unexpected shape."

            let feeds =
                feedsElement.EnumerateObject()
                |> Seq.choose (fun property ->
                    match Int32.TryParse(property.Name) with
                    | false, _ -> None
                    | true, id ->
                        let mutable address = Unchecked.defaultof<JsonElement>
                        let mutable folder = Unchecked.defaultof<JsonElement>

                        if not (property.Value.TryGetProperty("feed_address", &address)) then
                            None
                        else
                            let addressValue = address.GetString()
                            let folderValue =
                                if property.Value.TryGetProperty("folder", &folder) && folder.ValueKind = JsonValueKind.String then
                                    folder.GetString()
                                else
                                    match foldersByFeed.TryGetValue(id) with
                                    | true, value -> value
                                    | _ -> ""

                            Some { Id = id; Address = addressValue; Folder = folderValue })
                |> List.ofSeq

            return { Feeds = feeds; Folders = folders }
        }

    member _.AddFeed(url: string, folder: string, existingFolders: Set<string>) =
        task {
            let! folders = addFolderPath folder existingFolders
            let! body =
                postForm
                    "reader/add_url"
                    [ KeyValuePair("url", url)
                      KeyValuePair("folder", folder) ]

            if parseApiCode body <= 0 then
                invalidOp $"NewsBlur could not add feed {url}."

            use json = JsonDocument.Parse(body)
            let root = json.RootElement
            let mutable feed = Unchecked.defaultof<JsonElement>

            if not (root.TryGetProperty("feed", &feed)) then
                invalidOp "NewsBlur add feed response did not contain feed details."

            let mutable idElement = Unchecked.defaultof<JsonElement>
            let mutable addressElement = Unchecked.defaultof<JsonElement>

            let mutable id = 0

            if not (feed.TryGetProperty("id", &idElement) && idElement.TryGetInt32(&id)) then
                invalidOp "NewsBlur add feed response did not contain a numeric feed id."

            let address =
                if feed.TryGetProperty("feed_address", &addressElement) && addressElement.ValueKind = JsonValueKind.String then
                    addressElement.GetString()
                else
                    url

            return { Id = id; Address = address }, folders
        }

    member _.MoveFeed(feedId: int, fromFolder: string, toFolder: string, existingFolders: Set<string>) =
        task {
            let! folders = addFolderPath toFolder existingFolders
            let! body =
                postForm
                    "reader/move_feed_to_folder"
                    [ KeyValuePair("feed_id", string feedId)
                      KeyValuePair("in_folder", fromFolder)
                      KeyValuePair("to_folder", toFolder) ]

            if parseApiCode body <= 0 then
                invalidOp $"NewsBlur could not move feed {feedId} to the requested folder."

            return folders
        }

    member _.DeleteFeed(feedId: int, folder: string) =
        task {
            let! body =
                postForm
                    "reader/delete_feed"
                    [ KeyValuePair("feed_id", string feedId)
                      KeyValuePair("in_folder", folder) ]

            if parseApiCode body <= 0 then
                invalidOp $"NewsBlur could not remove managed feed {feedId}."
        }
