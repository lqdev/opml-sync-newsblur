namespace OpmlSyncNewsBlur

open System

module UrlNormalization =
    let normalize (value: string) =
        match Uri.TryCreate(value, UriKind.Absolute) with
        | false, _ -> invalidArg (nameof value) "Feed URL must be an absolute URL."
        | true, uri when uri.Scheme <> Uri.UriSchemeHttps && uri.Scheme <> Uri.UriSchemeHttp ->
            invalidArg (nameof value) "Feed URL must use HTTP or HTTPS."
        | true, uri when not (String.IsNullOrEmpty(uri.UserInfo)) ->
            invalidArg (nameof value) "Feed URL cannot contain embedded credentials."
        | true, uri ->
            let builder = UriBuilder(uri)
            builder.Scheme <- uri.Scheme.ToLowerInvariant()
            builder.Host <- uri.Host.ToLowerInvariant()
            builder.Fragment <- ""

            let query =
                uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                |> Array.choose (fun parameter ->
                    let parts = parameter.Split('=', 2)
                    let key = Uri.UnescapeDataString(parts[0].Replace("+", " "))
                    let value = if parts.Length = 2 then Uri.UnescapeDataString(parts[1].Replace("+", " ")) else ""

                    if key.StartsWith("utm_", StringComparison.OrdinalIgnoreCase) then
                        None
                    else
                        Some(Uri.EscapeDataString(key), Uri.EscapeDataString(value)))
                |> Array.sortBy id
                |> Array.map (fun (key, value) -> if value = "" then key else $"{key}={value}")

            builder.Query <- String.Join("&", query)

            let path = builder.Path.TrimEnd('/')
            builder.Path <- if String.IsNullOrEmpty(path) then "/" else path

            let normalizedUri = builder.Uri
            let normalizedPath =
                if String.IsNullOrEmpty(normalizedUri.AbsolutePath.Trim('/')) then
                    "/"
                else
                    normalizedUri.AbsolutePath.TrimEnd('/')

            normalizedUri.GetLeftPart(UriPartial.Authority)
            + normalizedPath
            + normalizedUri.Query
