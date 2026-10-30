namespace OpmlSyncNewsBlur

open System

type SyncConfig =
    { CallbackUrl: Uri
      HubUrl: Uri
      RootTopics: Uri list
      AllowedIncludeHosts: Set<string>
      MaxIncludeDepth: int
      LeaseSeconds: int
      MaxOpmlBytes: int64
      NewsBlurUsername: string
      NewsBlurPassword: string
      DryRun: bool }

module Config =
    let private value (name: string) =
        Environment.GetEnvironmentVariable(name) |> Option.ofObj

    let private requiredUri name =
        value name
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.defaultWith (fun () -> invalidOp $"Missing required setting {name}.")
        |> fun raw ->
            match Uri.TryCreate(raw, UriKind.Absolute) with
            | true, uri when uri.Scheme = Uri.UriSchemeHttps || (uri.Scheme = Uri.UriSchemeHttp && uri.IsLoopback) ->
                uri
            | _ -> invalidOp $"{name} must be an absolute HTTPS URL (or loopback HTTP for local development)."

    let private parseUris name defaultValue =
        value name
        |> Option.defaultValue defaultValue
        |> fun raw ->
            raw.Split(',', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
            |> Array.map (fun item ->
                match Uri.TryCreate(item, UriKind.Absolute) with
                | true, uri
                    when uri.Scheme = Uri.UriSchemeHttps
                         && String.IsNullOrEmpty(uri.UserInfo)
                         && String.IsNullOrEmpty(uri.Query)
                         && String.IsNullOrEmpty(uri.Fragment) ->
                    uri
                | _ -> invalidOp $"Invalid URL in {name}: {item}")
        |> fun roots ->
            if roots.Length = 0 then
                invalidOp $"{name} must contain at least one absolute HTTPS URL."

            Array.toList roots

    let private parseHosts name defaultValue =
        value name
        |> Option.defaultValue defaultValue
        |> fun raw ->
            raw.Split(',', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
            |> Array.map (fun host -> host.ToLowerInvariant())
            |> Set.ofArray

    let private parseInt name defaultValue minimum maximum =
        match value name with
        | None -> defaultValue
        | Some raw ->
            match Int32.TryParse(raw) with
            | true, parsed when parsed >= minimum && parsed <= maximum -> parsed
            | _ -> invalidOp $"{name} must be an integer between {minimum} and {maximum}."

    let private parseBool name defaultValue =
        match value name with
        | None -> defaultValue
        | Some raw ->
            match Boolean.TryParse(raw) with
            | true, parsed -> parsed
            | _ -> invalidOp $"{name} must be true or false."

    let load () =
        let callback = requiredUri "SYNC_CALLBACK_URL"
        let hub = requiredUri "WEBSUB_HUB_URL"
        let dryRun = parseBool "SYNC_DRY_RUN" true
        let username = value "NEWSBLUR_USERNAME" |> Option.defaultValue ""
        let password = value "NEWSBLUR_PASSWORD" |> Option.defaultValue ""

        if not (
            String.IsNullOrEmpty(callback.UserInfo)
            && callback.AbsolutePath = "/callback"
            && String.IsNullOrEmpty(callback.Query)
            && String.IsNullOrEmpty(callback.Fragment)
        ) then
            invalidOp "SYNC_CALLBACK_URL must use the /callback route."

        if not (
            String.IsNullOrEmpty(hub.UserInfo)
            && hub.AbsolutePath = "/"
            && String.IsNullOrEmpty(hub.Query)
            && String.IsNullOrEmpty(hub.Fragment)
        ) then
            invalidOp "WEBSUB_HUB_URL must point to the hub root."

        if not dryRun && (String.IsNullOrWhiteSpace(username) || String.IsNullOrWhiteSpace(password)) then
            invalidOp "NEWSBLUR_USERNAME and NEWSBLUR_PASSWORD are required when SYNC_DRY_RUN is false."

        let allowedHosts =
            parseHosts
                "WEBSUB_ALLOWED_INCLUDE_HOSTS"
                "lqdev.me,www.lqdev.me,luisquintanilla.me,www.luisquintanilla.me"

        let rootTopics =
            parseUris
                "WEBSUB_ROOT_TOPICS"
                "https://lqdev.me/.well-known/following.opml"

        if rootTopics |> List.exists (fun topic -> not (allowedHosts.Contains(topic.Host.ToLowerInvariant()))) then
            invalidOp "Every WEBSUB_ROOT_TOPICS host must be present in WEBSUB_ALLOWED_INCLUDE_HOSTS."

        { CallbackUrl = callback
          HubUrl = hub
          RootTopics = rootTopics
          AllowedIncludeHosts = allowedHosts
          MaxIncludeDepth = parseInt "WEBSUB_MAX_INCLUDE_DEPTH" 3 1 10
          LeaseSeconds = parseInt "WEBSUB_LEASE_SECONDS" 2592000 3600 2592000
          MaxOpmlBytes = 5L * 1024L * 1024L
          NewsBlurUsername = username
          NewsBlurPassword = password
          DryRun = dryRun }
