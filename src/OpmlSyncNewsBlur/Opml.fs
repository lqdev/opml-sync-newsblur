namespace OpmlSyncNewsBlur

open System
open System.Collections.Generic
open System.IO
open System.Net.Http
open System.Text
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open System.Xml.Linq

module Opml =
    let private linkPattern =
        Regex("<(?<url>[^>]+)>\\s*;\\s*rel\\s*=\\s*\"?(?<rel>[^\";,]+)\"?", RegexOptions.IgnoreCase)

    let private attribute (name: string) (element: XElement) =
        let value = element.Attribute(XName.Get name)
        if isNull value then "" else value.Value

    let private displayText (outline: XElement) =
        let title = attribute "title" outline
        if String.IsNullOrWhiteSpace(title) then attribute "text" outline else title

    let private parseLink (relation: string) (values: seq<string>) =
        values
        |> Seq.collect (fun value -> linkPattern.Matches(value) |> Seq.cast<Match>)
        |> Seq.tryPick (fun link ->
            if link.Groups["rel"].Value.Equals(relation, StringComparison.OrdinalIgnoreCase) then
                match Uri.TryCreate(link.Groups["url"].Value, UriKind.Absolute) with
                | true, uri -> Some uri
                | _ -> None
            else
                None)

    let parseLinkHeader relation values = parseLink relation values

    let private readLimited (stream: Stream) (maximumBytes: int64) (ct: CancellationToken) =
        task {
            use output = new MemoryStream()
            let buffer = Array.zeroCreate<byte> 32768
            let mutable total = 0L
            let mutable count = 1

            while count > 0 do
                let! bytesRead = stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)
                count <- bytesRead
                total <- total + int64 bytesRead

                if total > maximumBytes then
                    raise (InvalidDataException($"OPML document exceeds the configured {maximumBytes}-byte limit."))

                if bytesRead > 0 then
                    do! output.WriteAsync(buffer.AsMemory(0, bytesRead), ct)

            return output.ToArray()
        }

    let fetchDocument (config: SyncConfig) (client: HttpClient) (uri: Uri) (ct: CancellationToken) =
        task {
            let! safe = UrlPolicy.isSafeAsync uri ct

            if not safe then
                raise (InvalidOperationException($"Refusing to fetch non-public OPML URL: {uri}"))

            use request = new HttpRequestMessage(HttpMethod.Get, uri)
            request.Headers.UserAgent.ParseAdd("opml-sync-newsblur/1.0")
            use! response = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)

            if int response.StatusCode >= 300 && int response.StatusCode < 400 then
                raise (HttpRequestException("OPML redirects are not followed by this subscriber."))

            response.EnsureSuccessStatusCode() |> ignore

            let mediaType =
                if isNull response.Content.Headers.ContentType then ""
                else response.Content.Headers.ContentType.MediaType

            if not (mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase)
                    || mediaType.Contains("opml", StringComparison.OrdinalIgnoreCase)) then
                raise (InvalidDataException($"OPML topic returned unsupported media type '{mediaType}'."))

            let contentLength = response.Content.Headers.ContentLength

            if contentLength.HasValue && contentLength.Value > config.MaxOpmlBytes then
                raise (InvalidDataException($"OPML document exceeds the configured {config.MaxOpmlBytes}-byte limit."))

            let linkValues =
                let mutable values = Unchecked.defaultof<IEnumerable<string>>
                if response.Headers.TryGetValues("Link", &values) then values |> Seq.toArray else [||]

            let discoveredHub = parseLink "hub" linkValues

            match discoveredHub with
            | Some hub when hub <> config.HubUrl ->
                raise (InvalidOperationException("OPML topic advertises a hub different from the configured trusted hub."))
            | _ -> ()

            use! stream = response.Content.ReadAsStreamAsync(ct)
            let! body = readLimited stream config.MaxOpmlBytes ct

            return
                { Body = Encoding.UTF8.GetString(body)
                  HubUrl = config.HubUrl }
        }

    let flatten
        (maximumDepth: int)
        (allowedHosts: Set<string>)
        (fetch: Uri -> Task<FetchedOpml>)
        (rootTopic: Uri)
        : Task<FlattenedOpml> =
        task {
            let! rootDocument = fetch rootTopic
            let feeds = Dictionary<string, OpmlFeed>(StringComparer.Ordinal)
            let includedTopics = Dictionary<string, TopicCandidate>(StringComparer.Ordinal)
            let activeTopics = HashSet<string>(StringComparer.Ordinal)

            let rec visit (topic: Uri) (document: FetchedOpml) (depth: int) (folderPath: string list) =
                task {
                    if depth > maximumDepth then
                        raise (InvalidOperationException($"OPML include nesting exceeds the configured depth of {maximumDepth}."))

                    let parsed = XDocument.Parse(document.Body)

                    if isNull parsed.Root || parsed.Root.Name.LocalName <> "opml" then
                        raise (InvalidDataException("Included document is not an OPML document."))

                    let body = parsed.Root.Element(XName.Get "body")

                    if isNull body then
                        raise (InvalidDataException("OPML document has no body element."))

                    let rec visitOutlines (parent: XElement) (currentFolderPath: string list) =
                        task {
                            for outline in parent.Elements(XName.Get "outline") do
                                let title = displayText outline
                                let outlineType = attribute "type" outline
                                let xmlUrl = attribute "xmlUrl" outline

                                if outlineType.Equals("include", StringComparison.OrdinalIgnoreCase) then
                                    let rawUrl = attribute "url" outline

                                    match Uri.TryCreate(rawUrl, UriKind.Absolute) with
                                    | true, childUri
                                        when childUri.Scheme = Uri.UriSchemeHttps
                                             && String.IsNullOrEmpty(childUri.UserInfo)
                                             && allowedHosts.Contains(childUri.Host.ToLowerInvariant()) ->
                                        if depth >= maximumDepth then
                                            raise (InvalidOperationException($"OPML include depth exceeds {maximumDepth} at {childUri}."))

                                        let childFolder =
                                            if String.IsNullOrWhiteSpace(title) then currentFolderPath
                                            else currentFolderPath @ [ title ]

                                        let childTopic = childUri.AbsoluteUri

                                        if not (includedTopics.ContainsKey(childTopic)) then
                                            includedTopics.Add(
                                                childTopic,
                                                { Topic = childTopic
                                                  HubUrl = document.HubUrl.AbsoluteUri
                                                  FolderPrefix = String.Join("/", childFolder) }
                                            )

                                        if not (activeTopics.Contains(childTopic)) then
                                            let! childDocument = fetch childUri
                                            activeTopics.Add(childTopic) |> ignore
                                            do! visit childUri childDocument (depth + 1) childFolder
                                            activeTopics.Remove(childTopic) |> ignore
                                    | _ ->
                                        raise (InvalidOperationException($"OPML include URL is invalid or its host is not allowed: {rawUrl}"))
                                elif not (String.IsNullOrWhiteSpace(xmlUrl)) then
                                    match Uri.TryCreate(xmlUrl, UriKind.Absolute) with
                                    | true, feedUri when feedUri.Scheme = Uri.UriSchemeHttp || feedUri.Scheme = Uri.UriSchemeHttps ->
                                        let normalized = UrlNormalization.normalize feedUri.AbsoluteUri

                                        if not (feeds.ContainsKey(normalized)) then
                                            feeds.Add(
                                                normalized,
                                                { Title = if String.IsNullOrWhiteSpace(title) then feedUri.Host else title
                                                  Url = feedUri.AbsoluteUri
                                                  Folder = String.Join("/", currentFolderPath) }
                                            )
                                    | _ ->
                                        raise (InvalidDataException($"OPML feed URL is invalid: {xmlUrl}"))
                                elif outline.HasElements then
                                    let nextPath =
                                        if String.IsNullOrWhiteSpace(title) then currentFolderPath
                                        else currentFolderPath @ [ title ]

                                    do! visitOutlines outline nextPath
                        }

                    do! visitOutlines body folderPath
                }

            activeTopics.Add(rootTopic.AbsoluteUri) |> ignore
            do! visit rootTopic rootDocument 0 []

            return
                { Feeds = feeds.Values |> Seq.toList
                  IncludedTopics = includedTopics.Values |> Seq.toList }
        }
