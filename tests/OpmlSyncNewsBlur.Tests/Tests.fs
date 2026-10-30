namespace OpmlSyncNewsBlur.Tests

open System
open System.Net.Http
open System.Net
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open OpmlSyncNewsBlur

type private NewsBlurStubHandler() =
    inherit HttpMessageHandler()

    let requests = ResizeArray<string * string>()

    member _.Requests = requests.ToArray()

    override _.SendAsync(request: HttpRequestMessage, _: CancellationToken) =
        task {
            let! form =
                if isNull request.Content then
                    Task.FromResult("")
                else
                    request.Content.ReadAsStringAsync()

            requests.Add(request.RequestUri.AbsolutePath, form)

            let body =
                match request.RequestUri.AbsolutePath with
                | "/api/login" -> """{"authenticated":true}"""
                | "/reader/feeds" ->
                    """{"feeds":{"12":{"feed_address":"https://example.net/feed.xml"}},"flat_folders":{"Technology/Blogs":[12],"":[13]}}"""
                | _ -> """{"code":1}"""

            let response = new HttpResponseMessage(HttpStatusCode.OK)
            response.Content <- new StringContent(body, Encoding.UTF8, "application/json")
            return response
        }

module Tests =
    [<Fact>]
    let ``URL normalization removes fragments and tracking parameters`` () =
        let actual = UrlNormalization.normalize "HTTPS://Example.COM/feed/?utm_source=reader&b=2&a=1#section"
        Assert.Equal("https://example.com/feed?a=1&b=2", actual)

    [<Theory>]
    [<InlineData("127.0.0.1")>]
    [<InlineData("10.2.3.4")>]
    [<InlineData("192.168.1.1")>]
    [<InlineData("169.254.169.254")>]
    [<InlineData("::1")>]
    [<InlineData("fc00::1")>]
    let ``private and link-local addresses are rejected`` (value: string) =
        Assert.True(UrlPolicy.isPrivateAddress(IPAddress.Parse(value)))

    [<Theory>]
    [<InlineData("8.8.8.8")>]
    [<InlineData("2606:4700:4700::1111")>]
    let ``public addresses are allowed`` (value: string) =
        Assert.False(UrlPolicy.isPrivateAddress(IPAddress.Parse(value)))

    [<Fact>]
    let ``signature verification accepts only the matching body`` () =
        let secret = "test-secret"
        let body = Encoding.UTF8.GetBytes("<opml />")
        let signature =
            use hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret))
            "sha256=" + (hmac.ComputeHash(body) |> Convert.ToHexString |> fun value -> value.ToLowerInvariant())

        Assert.True(Signer.verify secret body signature)
        Assert.False(Signer.verify secret (Encoding.UTF8.GetBytes("<opml/>")) signature)
        Assert.False(Signer.verify secret body (signature.ToUpperInvariant()))

    [<Fact>]
    let ``NewsBlur feed snapshots read flat folder membership`` () =
        task {
            let config =
                { CallbackUrl = Uri("https://bridge.example/callback")
                  HubUrl = Uri("https://hub.example/")
                  RootTopics = []
                  AllowedIncludeHosts = Set.empty
                  MaxIncludeDepth = 3
                  LeaseSeconds = 86400
                  MaxOpmlBytes = 5L * 1024L * 1024L
                  NewsBlurUsername = "test-user"
                  NewsBlurPassword = "test-password"
                  DryRun = false }

            use transport = new HttpClient(new NewsBlurStubHandler())
            transport.BaseAddress <- Uri("https://newsblur.com/")
            let client = NewsBlurClient(config, transport)
            let! snapshot = client.GetFeeds()

            Assert.Contains(snapshot.Feeds, fun feed -> feed.Id = 12 && feed.Folder = "Technology/Blogs")
            Assert.Contains("Technology", snapshot.Folders)
            Assert.Contains("Technology/Blogs", snapshot.Folders)
        }

    [<Fact>]
    let ``NewsBlur managed feed removal uses the feed id endpoint`` () =
        task {
            let config =
                { CallbackUrl = Uri("https://bridge.example/callback")
                  HubUrl = Uri("https://hub.example/")
                  RootTopics = []
                  AllowedIncludeHosts = Set.empty
                  MaxIncludeDepth = 3
                  LeaseSeconds = 86400
                  MaxOpmlBytes = 5L * 1024L * 1024L
                  NewsBlurUsername = "test-user"
                  NewsBlurPassword = "test-password"
                  DryRun = false }

            let handler = new NewsBlurStubHandler()
            use transport = new HttpClient(handler)
            transport.BaseAddress <- Uri("https://www.newsblur.com/")
            let client = NewsBlurClient(config, transport)
            do! client.DeleteFeed(12, "Technology")

            let path, form =
                handler.Requests
                |> Array.find (fun (path, _) -> path = "/reader/delete_feed")

            Assert.Contains("feed_id=12", form)
            Assert.Contains("in_folder=Technology", form)
            Assert.Equal("/reader/delete_feed", path)
        }

    [<Fact>]
    let ``reconciliation queue messages round-trip through JSON`` () =
        let expected = { Topic = "https://lqdev.me/following.opml"; ContentHash = "abc123" }
        let json = JsonSerializer.Serialize(expected)
        let actual = JsonSerializer.Deserialize<ReconcileMessage>(json)

        Assert.Equal(expected.Topic, actual.Topic)
        Assert.Equal(expected.ContentHash, actual.ContentHash)

    [<Fact>]
    let ``flatten follows nested includes and preserves collection folders`` () =
        task {
            let root = Uri("https://lqdev.me/.well-known/following.opml")
            let child = Uri("https://lqdev.me/collections/technology.opml")

            let rootBody =
                """<opml version="2.0"><body>
                    <outline text="Technology" type="include" url="https://lqdev.me/collections/technology.opml" />
                    <outline text="Personal" type="rss" xmlUrl="https://example.net/personal.xml" />
                </body></opml>"""

            let childBody =
                """<opml version="2.0"><body>
                    <outline text="F# blog" type="rss" xmlUrl="https://example.net/fsharp.xml?utm_source=opml" />
                </body></opml>"""

            let documents =
                Map.ofList
                    [ root.AbsoluteUri, { Body = rootBody; HubUrl = Uri("https://hub.example/") }
                      child.AbsoluteUri, { Body = childBody; HubUrl = Uri("https://hub.example/") } ]

            let fetch (uri: Uri) = Task.FromResult(documents[uri.AbsoluteUri])
            let! result = Opml.flatten 3 (Set.ofList [ "lqdev.me" ]) fetch root

            Assert.Equal(2, result.Feeds.Length)
            Assert.Contains(result.Feeds, fun feed -> feed.Title = "F# blog" && feed.Folder = "Technology")
            Assert.Contains(result.Feeds, fun feed -> feed.Title = "Personal" && feed.Folder = "")
            Assert.Single(result.IncludedTopics) |> ignore
            Assert.Equal(child.AbsoluteUri, result.IncludedTopics.Head.Topic)
        }

    [<Fact>]
    let ``flatten rejects an include outside the host allowlist`` () =
        task {
            let root = Uri("https://lqdev.me/.well-known/following.opml")
            let body =
                """<opml version="2.0"><body>
                    <outline text="External" type="include" url="https://attacker.example/list.opml" />
                </body></opml>"""

            let fetch _ = Task.FromResult({ Body = body; HubUrl = Uri("https://hub.example/") })
            let mutable rejected = false

            try
                let! _ = Opml.flatten 3 (Set.ofList [ "lqdev.me" ]) fetch root
                ()
            with :? InvalidOperationException ->
                rejected <- true

            Assert.True(rejected)
        }

    [<Fact>]
    let ``include depth is bounded`` () =
        task {
            let root = Uri("https://lqdev.me/root.opml")
            let child = Uri("https://lqdev.me/child.opml")
            let grandchild = Uri("https://lqdev.me/grandchild.opml")

            let documents =
                Map.ofList
                    [ root.AbsoluteUri,
                      { Body = """<opml><body><outline text="One" type="include" url="https://lqdev.me/child.opml" /></body></opml>"""
                        HubUrl = Uri("https://hub.example/") }
                      child.AbsoluteUri,
                      { Body = """<opml><body><outline text="Two" type="include" url="https://lqdev.me/grandchild.opml" /></body></opml>"""
                        HubUrl = Uri("https://hub.example/") }
                      grandchild.AbsoluteUri,
                      { Body = """<opml><body><outline text="Feed" type="rss" xmlUrl="https://example.net/feed.xml" /></body></opml>"""
                        HubUrl = Uri("https://hub.example/") } ]

            let fetch (uri: Uri) = Task.FromResult(documents[uri.AbsoluteUri])
            let mutable rejected = false

            try
                let! _ = Opml.flatten 1 (Set.ofList [ "lqdev.me" ]) fetch root
                ()
            with :? InvalidOperationException ->
                rejected <- true

            Assert.True(rejected)
        }
