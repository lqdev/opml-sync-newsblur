namespace OpmlSyncNewsBlur

open System
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging

type SyncEngine
    (
        config: SyncConfig,
        tables: Storage.TableStore,
        blobs: Storage.BlobStore,
        hub: HubClient,
        newsBlur: NewsBlurClient,
        client: System.Net.Http.HttpClient,
        logger: ILogger<SyncEngine>
    ) =

    let fetchDocument uri = Opml.fetchDocument config client uri CancellationToken.None

    let hashBytes (bytes: byte array) =
        SHA256.HashData(bytes) |> Convert.ToHexString |> fun hex -> hex.ToLowerInvariant()

    let ensureRoot (root: Uri) =
        task {
            let! existing = tables.GetTopic(root.AbsoluteUri)
            let now = DateTimeOffset.UtcNow

            let state =
                match existing with
                | Some topic ->
                    { topic with
                        HubUrl = config.HubUrl.AbsoluteUri
                        IsRoot = true
                        PendingUnsubscribe = false }
                | None ->
                    { Topic = root.AbsoluteUri
                      HubUrl = config.HubUrl.AbsoluteUri
                      Secret = Signer.randomSecret ()
                      ExpiresAt = DateTimeOffset.MinValue
                      CreatedAt = now
                      Owners = []
                      IsRoot = true
                      PendingUnsubscribe = false
                      FolderPrefix = ""
                      LastHash = None }

            do! tables.UpsertTopic(state)

            if state.ExpiresAt <= now.AddHours(24.0) then
                do! hub.Subscribe(state)

            return state
        }

    let subscribeCandidate (root: string) (candidate: TopicCandidate) =
        task {
            let! existing = tables.GetTopic(candidate.Topic)
            let now = DateTimeOffset.UtcNow

            let state =
                match existing with
                | Some topic ->
                    { topic with
                        HubUrl = candidate.HubUrl
                        Owners = topic.Owners |> List.filter ((<>) root) |> fun owners -> root :: owners
                        PendingUnsubscribe = false
                        FolderPrefix = candidate.FolderPrefix }
                | None ->
                    { Topic = candidate.Topic
                      HubUrl = candidate.HubUrl
                      Secret = Signer.randomSecret ()
                      ExpiresAt = DateTimeOffset.MinValue
                      CreatedAt = now
                      Owners = [ root ]
                      IsRoot = false
                      PendingUnsubscribe = false
                      FolderPrefix = candidate.FolderPrefix
                      LastHash = None }

            do! tables.UpsertTopic(state)

            if existing.IsNone || state.ExpiresAt <= now.AddHours(24.0) then
                do! hub.Subscribe(state)
        }

    let updateTopicGraph (root: string) (candidates: TopicCandidate list) =
        task {
            let wanted = candidates |> List.map _.Topic |> Set.ofList
            let! current = tables.ListTopics()
            let currentOwners =
                current
                |> List.filter (fun topic -> not topic.IsRoot && topic.Owners |> List.contains root)

            for candidate in candidates do
                do! subscribeCandidate root candidate

            for topic in currentOwners do
                if not (wanted.Contains(topic.Topic)) then
                    let owners = topic.Owners |> List.filter ((<>) root)
                    let updated =
                        { topic with
                            Owners = owners
                            PendingUnsubscribe = owners.IsEmpty }

                    do! tables.UpsertTopic(updated)

                    if owners.IsEmpty then
                        do! hub.Unsubscribe(updated)
        }

    let reconcileFeeds (root: string) (desiredFeeds: OpmlFeed list) =
        task {
            let desired =
                desiredFeeds
                |> List.map (fun feed -> UrlNormalization.normalize feed.Url, feed)
                |> Map.ofList

            let! managed = tables.ListManagedFeeds()
            let owned = managed |> List.filter (fun feed -> feed.Owners |> List.contains root)
            let removed =
                owned
                |> List.filter (fun feed -> not (desired.ContainsKey(feed.NormalizedUrl)))

            let added =
                desired
                |> Map.toList
                |> List.filter (fun (normalized, _) -> not (owned |> List.exists (fun feed -> feed.NormalizedUrl = normalized)))

            if config.DryRun then
                logger.LogInformation(
                    "Dry run for {Root}: {AddCount} feed(s) would be considered for addition; {RemoveCount} managed feed(s) would be removed.",
                    root,
                    added.Length,
                    removed.Length
                )
            else
                let! snapshot = newsBlur.GetFeeds()
                let existingByUrl =
                    snapshot.Feeds
                    |> List.map (fun feed -> UrlNormalization.normalize feed.Address, feed)
                    |> Map.ofList
                let managedByUrl = managed |> List.map (fun feed -> feed.NormalizedUrl, feed) |> Map.ofList
                let mutable knownFolders = snapshot.Folders
                let mutable currentFeeds = existingByUrl

                for normalizedUrl, desiredFeed in desired |> Map.toList do
                    match Map.tryFind normalizedUrl managedByUrl with
                    | Some managedFeed ->
                        let owners = managedFeed.Owners |> List.filter ((<>) root) |> fun values -> root :: values

                        match Map.tryFind normalizedUrl currentFeeds with
                        | Some existingFeed ->
                            let! folder =
                                if existingFeed.Folder <> desiredFeed.Folder then
                                    task {
                                        let! folders =
                                            newsBlur.MoveFeed(
                                                existingFeed.Id,
                                                existingFeed.Folder,
                                                desiredFeed.Folder,
                                                knownFolders
                                            )

                                        knownFolders <- folders
                                        return desiredFeed.Folder
                                    }
                                else
                                    Task.FromResult(existingFeed.Folder)

                            do!
                                tables.UpsertManagedFeed
                                    { managedFeed with
                                        FeedAddress = existingFeed.Address
                                        FeedId = existingFeed.Id
                                        Folder = folder
                                        Owners = owners }
                        | None ->
                            let! addedFeed, folders = newsBlur.AddFeed(desiredFeed.Url, desiredFeed.Folder, knownFolders)
                            knownFolders <- folders
                            currentFeeds <- currentFeeds.Add(normalizedUrl, { Id = addedFeed.Id; Address = addedFeed.Address; Folder = desiredFeed.Folder })

                            do!
                                tables.UpsertManagedFeed
                                    { managedFeed with
                                        FeedAddress = addedFeed.Address
                                        FeedId = addedFeed.Id
                                        Folder = desiredFeed.Folder
                                        Owners = owners }
                    | None ->
                        match Map.tryFind normalizedUrl currentFeeds with
                        | Some _ ->
                            ()
                        | None ->
                            let! addedFeed, folders = newsBlur.AddFeed(desiredFeed.Url, desiredFeed.Folder, knownFolders)
                            knownFolders <- folders
                            currentFeeds <- currentFeeds.Add(normalizedUrl, { Id = addedFeed.Id; Address = addedFeed.Address; Folder = desiredFeed.Folder })

                            do!
                                tables.UpsertManagedFeed
                                    { NormalizedUrl = normalizedUrl
                                      FeedAddress = addedFeed.Address
                                      FeedId = addedFeed.Id
                                      Folder = desiredFeed.Folder
                                      Owners = [ root ] }

                for managedFeed in removed do
                    let remainingOwners = managedFeed.Owners |> List.filter ((<>) root)

                    if remainingOwners.IsEmpty then
                        match Map.tryFind managedFeed.NormalizedUrl currentFeeds with
                        | Some existingFeed ->
                            do! newsBlur.DeleteFeed(existingFeed.Id, existingFeed.Folder)
                        | None ->
                            ()

                        do! tables.DeleteManagedFeed(managedFeed.NormalizedUrl)
                    else
                        do! tables.UpsertManagedFeed { managedFeed with Owners = remainingOwners }
        }

    let reconcileRoot (root: Uri) (overrideTopic: (string * string) option) =
        task {
            let! _ = ensureRoot root

            let fetchForReconcile (topic: Uri) : Task<FetchedOpml> =
                match overrideTopic with
                | Some(changedTopic, contentHash) when topic.AbsoluteUri = changedTopic ->
                    task {
                        let! bytes = blobs.Get(contentHash)
                        let! topicState = tables.GetTopic(changedTopic)

                        match topicState with
                        | Some state ->
                            return
                                { Body = Encoding.UTF8.GetString(bytes)
                                  HubUrl = Uri(state.HubUrl) }
                        | None ->
                            return raise (InvalidOperationException($"Changed topic is not tracked: {changedTopic}"))
                    }
                | _ ->
                    fetchDocument topic

            let! flattened =
                Opml.flatten
                    config.MaxIncludeDepth
                    config.AllowedIncludeHosts
                    fetchForReconcile
                    root

            do! updateTopicGraph root.AbsoluteUri flattened.IncludedTopics
            do! reconcileFeeds root.AbsoluteUri flattened.Feeds
            logger.LogInformation(
                "Reconciled {FeedCount} desired feed(s) from OPML root {Root}",
                flattened.Feeds.Length,
                root.AbsoluteUri
            )
        }

    member _.Bootstrap() =
        task {
            for root in config.RootTopics do
                do! reconcileRoot root None
        }

    member _.Reconcile(message: ReconcileMessage) =
        task {
            let! changedTopic = tables.GetTopic(message.Topic)

            match changedTopic with
            | None ->
                logger.LogWarning("Ignored notification for an untracked OPML topic")
            | Some topic when topic.PendingUnsubscribe ->
                logger.LogInformation("Ignored notification for a topic pending unsubscribe")
            | Some topic ->
                match topic.LastHash with
                | Some previousHash when previousHash = message.ContentHash ->
                    logger.LogInformation("Ignored duplicate OPML notification for {Topic}", message.Topic)
                | _ ->
                    let roots = if topic.IsRoot then [ topic.Topic ] else topic.Owners

                    for root in roots do
                        do! reconcileRoot (Uri(root)) (Some(message.Topic, message.ContentHash))

                    if not roots.IsEmpty then
                        let! _ = tables.SetTopicHash(message.Topic, message.ContentHash)
                        ()
        }

    member _.Renew() =
        task {
            let now = DateTimeOffset.UtcNow
            let! topics = tables.ListTopics()

            for topic in topics do
                if topic.PendingUnsubscribe then
                    do! hub.Unsubscribe(topic)
                elif (topic.IsRoot || not topic.Owners.IsEmpty) && topic.ExpiresAt <= now.AddHours(24.0) then
                    do! hub.Subscribe(topic)

            do! blobs.DeleteOlderThan(now.AddDays(-7.0))
        }
