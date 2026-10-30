namespace OpmlSyncNewsBlur.Functions

open System
open System.Collections.Generic
open System.IO
open System.Net
open System.Security.Cryptography
open System.Text
open System.Threading.Tasks
open Microsoft.Azure.Functions.Worker
open Microsoft.Azure.Functions.Worker.Http
open Microsoft.Extensions.Logging
open OpmlSyncNewsBlur

type Callback(config: SyncConfig, tables: Storage.TableStore, blobs: Storage.BlobStore, queues: Storage.QueueStore, logger: ILogger<Callback>) =
    let response (request: HttpRequestData) status (body: string) =
        task {
            let result = request.CreateResponse(status)

            if not (String.IsNullOrEmpty(body)) then
                result.Headers.Add("Content-Type", "text/plain; charset=utf-8")
                do! result.WriteStringAsync(body)

            return result
        }

    let queryParameters (query: string) =
        query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        |> Array.choose (fun parameter ->
            let parts = parameter.Split('=', 2)

            if parts.Length = 0 then
                None
            else
                let key = Uri.UnescapeDataString(parts[0].Replace("+", " "))
                let value = if parts.Length > 1 then Uri.UnescapeDataString(parts[1].Replace("+", " ")) else ""
                Some(key, value))
        |> Array.groupBy fst
        |> Array.map (fun (key, values) -> key, values |> Array.map snd |> Array.tryHead |> Option.defaultValue "")
        |> Map.ofArray

    let headerValue (request: HttpRequestData) name =
        let mutable values = Unchecked.defaultof<IEnumerable<string>>
        if request.Headers.TryGetValues(name, &values) then values |> Seq.tryHead else None

    let readLimited (stream: Stream) maximumBytes =
        task {
            use output = new MemoryStream()
            let buffer = Array.zeroCreate<byte> 32768
            let mutable total = 0L
            let mutable count = 1
            let mutable tooLarge = false

            while count > 0 && not tooLarge do
                let! bytesRead = stream.ReadAsync(buffer.AsMemory(0, buffer.Length))
                count <- bytesRead
                total <- total + int64 bytesRead

                if total > maximumBytes then
                    tooLarge <- true

                if bytesRead > 0 then
                    do! output.WriteAsync(buffer.AsMemory(0, bytesRead))

            return if tooLarge then Error() else Ok(output.ToArray())
        }

    let verify (request: HttpRequestData) =
        task {
            let query = queryParameters request.Url.Query
            let tryGet name = Map.tryFind name query
            let mode = tryGet "hub.mode"
            let topic = tryGet "hub.topic"
            let challenge = tryGet "hub.challenge"

            match mode, topic, challenge with
            | Some ("subscribe" | "unsubscribe"), Some topicValue, Some challengeValue
                when not (String.IsNullOrWhiteSpace(challengeValue)) ->
                match Uri.TryCreate(topicValue, UriKind.Absolute) with
                | true, topicUri ->
                    let! state = tables.GetTopic(topicUri.AbsoluteUri)

                    match state with
                    | None ->
                        return! response request HttpStatusCode.NotFound ""
                    | Some stored when mode = Some "subscribe" && stored.PendingUnsubscribe ->
                        return! response request HttpStatusCode.NotFound ""
                    | Some stored when mode = Some "unsubscribe" && not stored.PendingUnsubscribe ->
                        return! response request HttpStatusCode.NotFound ""
                    | Some stored ->
                        match mode with
                        | Some "subscribe" ->
                            let leaseSeconds =
                                match tryGet "hub.lease_seconds" with
                                | Some raw ->
                                    match Int32.TryParse(raw) with
                                    | true, lease when lease > 0 -> min lease config.LeaseSeconds
                                    | _ -> config.LeaseSeconds
                                | None -> config.LeaseSeconds

                            do!
                                tables.UpsertTopic
                                    { stored with
                                        ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(float leaseSeconds)
                                        PendingUnsubscribe = false }
                        | Some "unsubscribe" ->
                            do! tables.DeleteTopic(stored.Topic)
                        | _ ->
                            ()

                        return! response request HttpStatusCode.OK challengeValue
                | _ ->
                    return! response request HttpStatusCode.BadRequest "hub.topic must be an absolute URL"
            | _ ->
                return! response request HttpStatusCode.BadRequest "hub.mode, hub.topic, and hub.challenge are required"
        }

    let receive (request: HttpRequestData) =
        task {
            let linkValues =
                let mutable values = Unchecked.defaultof<IEnumerable<string>>
                if request.Headers.TryGetValues("Link", &values) then values |> Seq.toArray else [||]

            match Opml.parseLinkHeader "self" linkValues with
            | None ->
                return! response request HttpStatusCode.BadRequest "delivery is missing Link rel=self"
            | Some topicUri ->
                let! state = tables.GetTopic(topicUri.AbsoluteUri)

                match state with
                | None ->
                    return! response request HttpStatusCode.Gone "topic is not subscribed"
                | Some subscription when subscription.PendingUnsubscribe ->
                    return! response request HttpStatusCode.Gone "topic is pending unsubscribe"
                | Some subscription ->
                    let signature = headerValue request "X-Hub-Signature"

                    match signature with
                    | None ->
                        return! response request HttpStatusCode.Unauthorized "missing X-Hub-Signature"
                    | Some signatureValue ->
                        let! bodyResult = readLimited request.Body config.MaxOpmlBytes

                        match bodyResult with
                        | Error () ->
                            return! response request HttpStatusCode.RequestEntityTooLarge "OPML delivery exceeds the configured payload limit"
                        | Ok body ->
                            if not (Signer.verify subscription.Secret body signatureValue) then
                                return! response request HttpStatusCode.Unauthorized "invalid X-Hub-Signature"
                            else
                                let mediaType =
                                    headerValue request "Content-Type"
                                    |> Option.defaultValue ""
                                    |> fun value -> (value.Split(';')[0]).Trim()

                                if not (
                                    mediaType.Contains("opml", StringComparison.OrdinalIgnoreCase)
                                    || mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase)
                                ) then
                                    return! response request HttpStatusCode.UnsupportedMediaType "only OPML/XML topics are supported"
                                else
                                    let contentHash =
                                        SHA256.HashData(body)
                                        |> Convert.ToHexString
                                        |> fun hex -> hex.ToLowerInvariant()

                                    do! blobs.Put(contentHash, body)
                                    do! queues.Enqueue({ Topic = subscription.Topic; ContentHash = contentHash })
                                    logger.LogInformation("Accepted a signed OPML update for a tracked topic")
                                    return! response request HttpStatusCode.Accepted ""
        }

    [<Function("Callback")>]
    member _.Run([<HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "callback")>] request: HttpRequestData) : Task<HttpResponseData> =
        match request.Method.ToUpperInvariant() with
        | "GET" -> verify request
        | "POST" -> receive request
        | _ -> response request HttpStatusCode.MethodNotAllowed ""
