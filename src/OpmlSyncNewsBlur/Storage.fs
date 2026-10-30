namespace OpmlSyncNewsBlur

open System
open System.Collections.Generic
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Azure
open Azure.Data.Tables
open Azure.Storage.Blobs
open Azure.Storage.Queues

module Storage =
    let hash (value: string) =
        SHA256.HashData(Encoding.UTF8.GetBytes(value))
        |> Convert.ToHexString
        |> fun hex -> hex.ToLowerInvariant()

    let private readAllAsync<'T> (items: IAsyncEnumerable<'T>) =
        task {
            let values = ResizeArray<'T>()
            let enumerator = items.GetAsyncEnumerator()
            let mutable hasNext = true

            while hasNext do
                let! hasValue = enumerator.MoveNextAsync().AsTask()

                if hasValue then
                    values.Add enumerator.Current
                else
                    hasNext <- false

            do! enumerator.DisposeAsync().AsTask()
            return List.ofSeq values
        }

    let private tryString (entity: TableEntity) (key: string) =
        match entity.TryGetValue(key) with
        | true, (:? string as value) -> Some value
        | _ -> None

    let private tryDate (entity: TableEntity) (key: string) =
        match entity.TryGetValue(key) with
        | true, (:? DateTimeOffset as value) -> Some value
        | true, (:? DateTime as value) -> Some(DateTimeOffset(value, TimeSpan.Zero))
        | _ -> None

    let private tryBool (entity: TableEntity) (key: string) =
        match entity.TryGetValue(key) with
        | true, (:? bool as value) -> Some value
        | _ -> None

    let private parseStrings (value: string option) =
        value
        |> Option.map (JsonSerializer.Deserialize<string array>)
        |> Option.defaultValue [||]
        |> Array.toList

    let private topicFromEntity (entity: TableEntity) =
        match
            tryString entity "Topic",
            tryString entity "HubUrl",
            tryString entity "Secret",
            tryDate entity "ExpiresAt",
            tryDate entity "CreatedAt"
        with
        | Some topic, Some hubUrl, Some secret, Some expiresAt, Some createdAt ->
            Some
                { Topic = topic
                  HubUrl = hubUrl
                  Secret = secret
                  ExpiresAt = expiresAt
                  CreatedAt = createdAt
                  Owners = parseStrings (tryString entity "Owners")
                  IsRoot = tryBool entity "IsRoot" |> Option.defaultValue false
                  PendingUnsubscribe = tryBool entity "PendingUnsubscribe" |> Option.defaultValue false
                  FolderPrefix = tryString entity "FolderPrefix" |> Option.defaultValue ""
                  LastHash = tryString entity "LastHash" }
        | _ ->
            None

    let private managedFeedFromEntity (entity: TableEntity) =
        match
            tryString entity "NormalizedUrl",
            tryString entity "FeedAddress",
            tryString entity "Folder",
            entity.TryGetValue("FeedId")
        with
        | Some normalizedUrl, Some feedAddress, Some folder, (true, (:? int as feedId)) ->
            Some
                { NormalizedUrl = normalizedUrl
                  FeedAddress = feedAddress
                  FeedId = feedId
                  Folder = folder
                  Owners = parseStrings (tryString entity "Owners") }
        | _ ->
            None

    type TableStore(service: TableServiceClient) =
        let topics = service.GetTableClient("topics")
        let managedFeeds = service.GetTableClient("managedfeeds")

        member _.EnsureTables() =
            task {
                let! _ = topics.CreateIfNotExistsAsync()
                let! _ = managedFeeds.CreateIfNotExistsAsync()
                return ()
            }

        member _.GetTopic(topic: string) =
            task {
                let! result = topics.GetEntityIfExistsAsync<TableEntity>("topic", hash topic)
                return if result.HasValue then topicFromEntity result.Value else None
            }

        member _.ListTopics() =
            task {
                let! entities = topics.QueryAsync<TableEntity>() |> readAllAsync
                return entities |> Seq.choose topicFromEntity |> List.ofSeq
            }

        member _.UpsertTopic(topic: TopicState) =
            task {
                let entity = TableEntity("topic", hash topic.Topic)
                entity["Topic"] <- topic.Topic
                entity["HubUrl"] <- topic.HubUrl
                entity["Secret"] <- topic.Secret
                entity["ExpiresAt"] <- topic.ExpiresAt
                entity["CreatedAt"] <- topic.CreatedAt
                entity["Owners"] <- JsonSerializer.Serialize(topic.Owners)
                entity["IsRoot"] <- topic.IsRoot
                entity["PendingUnsubscribe"] <- topic.PendingUnsubscribe
                entity["FolderPrefix"] <- topic.FolderPrefix

                match topic.LastHash with
                | Some contentHash -> entity["LastHash"] <- contentHash
                | None -> ()

                let! _ = topics.UpsertEntityAsync(entity, TableUpdateMode.Replace)
                return ()
            }

        member _.DeleteTopic(topic: string) =
            task {
                let! existing = topics.GetEntityIfExistsAsync<TableEntity>("topic", hash topic)

                if existing.HasValue then
                    let! _ = topics.DeleteEntityAsync("topic", hash topic, ETag.All)
                    ()

                return ()
            }

        member _.SetTopicHash(topic: string, contentHash: string) =
            task {
                let! state = topics.GetEntityIfExistsAsync<TableEntity>("topic", hash topic)

                if not state.HasValue then
                    return false
                else
                    state.Value["LastHash"] <- contentHash
                    let! _ = topics.UpsertEntityAsync(state.Value, TableUpdateMode.Merge)
                    return true
            }

        member _.ListManagedFeeds() =
            task {
                let! entities = managedFeeds.QueryAsync<TableEntity>() |> readAllAsync
                return entities |> Seq.choose managedFeedFromEntity |> List.ofSeq
            }

        member _.UpsertManagedFeed(feed: ManagedFeed) =
            task {
                let entity = TableEntity("feed", hash feed.NormalizedUrl)
                entity["NormalizedUrl"] <- feed.NormalizedUrl
                entity["FeedAddress"] <- feed.FeedAddress
                entity["FeedId"] <- feed.FeedId
                entity["Folder"] <- feed.Folder
                entity["Owners"] <- JsonSerializer.Serialize(feed.Owners)
                let! _ = managedFeeds.UpsertEntityAsync(entity, TableUpdateMode.Replace)
                return ()
            }

        member _.DeleteManagedFeed(normalizedUrl: string) =
            task {
                let! existing = managedFeeds.GetEntityIfExistsAsync<TableEntity>("feed", hash normalizedUrl)

                if existing.HasValue then
                    let! _ = managedFeeds.DeleteEntityAsync("feed", hash normalizedUrl, ETag.All)
                    ()

                return ()
            }

    type QueueStore(service: QueueServiceClient) =
        member _.EnsureQueues() =
            task {
                let! _ = service.GetQueueClient("opml-reconcile").CreateIfNotExistsAsync()
                return ()
            }

        member _.Enqueue<'T>(message: 'T, ?delay: TimeSpan) =
            task {
                let queue = service.GetQueueClient("opml-reconcile")
                let json = JsonSerializer.Serialize(message)
                let encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))

                let visibility =
                    delay
                    |> Option.map Nullable
                    |> Option.defaultValue (Nullable())

                let! _ = queue.SendMessageAsync(encoded, visibility, Nullable())
                return ()
            }

    type BlobStore(service: BlobServiceClient) =
        let container = service.GetBlobContainerClient("opml-payloads")

        member _.EnsureContainer() =
            task {
                let! _ = container.CreateIfNotExistsAsync()
                return ()
            }

        member _.Put(contentHash: string, body: byte array) =
            task {
                let! _ = container.GetBlobClient(contentHash).UploadAsync(BinaryData(body), true)
                return ()
            }

        member _.Get(contentHash: string) =
            task {
                let! result = container.GetBlobClient(contentHash).DownloadContentAsync()
                return result.Value.Content.ToArray()
            }

        member _.DeleteOlderThan(cutoff: DateTimeOffset) =
            task {
                let! items = container.GetBlobsAsync() |> readAllAsync

                for item in items do
                    match item.Properties.LastModified with
                    | lastModified when lastModified.HasValue && lastModified.Value < cutoff ->
                        let! _ = container.GetBlobClient(item.Name).DeleteIfExistsAsync()
                        ()
                    | _ -> ()
            }
