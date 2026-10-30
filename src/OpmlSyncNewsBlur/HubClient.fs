namespace OpmlSyncNewsBlur

open System
open System.Collections.Generic
open System.Net
open System.Net.Http

type HubClient(config: SyncConfig, client: HttpClient) =
    let postMode mode (topic: TopicState) =
        task {
            let fields =
                [ KeyValuePair("hub.mode", mode)
                  KeyValuePair("hub.topic", topic.Topic)
                  KeyValuePair("hub.callback", config.CallbackUrl.AbsoluteUri)
                  KeyValuePair("hub.verify", "async")
                  KeyValuePair("hub.lease_seconds", string config.LeaseSeconds)
                  KeyValuePair("hub.secret", topic.Secret) ]

            use request = new HttpRequestMessage(HttpMethod.Post, topic.HubUrl)
            request.Content <- new FormUrlEncodedContent(fields)
            use! response = client.SendAsync(request)

            if response.StatusCode <> HttpStatusCode.Accepted then
                raise (HttpRequestException($"WebSub hub rejected {mode} for topic {topic.Topic} (HTTP {int response.StatusCode})."))
        }

    member _.Subscribe(topic: TopicState) = postMode "subscribe" topic
    member _.Unsubscribe(topic: TopicState) = postMode "unsubscribe" topic
