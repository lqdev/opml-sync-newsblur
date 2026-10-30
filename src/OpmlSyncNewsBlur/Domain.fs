namespace OpmlSyncNewsBlur

open System

type TopicState =
    { Topic: string
      HubUrl: string
      Secret: string
      ExpiresAt: DateTimeOffset
      CreatedAt: DateTimeOffset
      Owners: string list
      IsRoot: bool
      PendingUnsubscribe: bool
      FolderPrefix: string
      LastHash: string option }

type ManagedFeed =
    { NormalizedUrl: string
      FeedAddress: string
      FeedId: int
      Folder: string
      Owners: string list }

type OpmlFeed =
    { Title: string
      Url: string
      Folder: string }

type TopicCandidate =
    { Topic: string
      HubUrl: string
      FolderPrefix: string }

type FlattenedOpml =
    { Feeds: OpmlFeed list
      IncludedTopics: TopicCandidate list }

type FetchedOpml =
    { Body: string
      HubUrl: Uri }

type ReconcileMessage =
    { Topic: string
      ContentHash: string }

type NewsBlurFeed =
    { Id: int
      Address: string
      Folder: string }

type NewsBlurAddedFeed =
    { Id: int
      Address: string }

type NewsBlurSnapshot =
    { Feeds: NewsBlurFeed list
      Folders: Set<string> }
