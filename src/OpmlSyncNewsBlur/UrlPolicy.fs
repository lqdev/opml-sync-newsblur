namespace OpmlSyncNewsBlur

open System
open System.Net
open System.Net.Sockets
open System.Threading
open System.Threading.Tasks

module UrlPolicy =
    let rec isPrivateAddress (address: IPAddress) =
        if address.AddressFamily = AddressFamily.InterNetworkV6 then
            if address.IsIPv4MappedToIPv6 then
                isPrivateAddress (address.MapToIPv4())
            else
                let bytes = address.GetAddressBytes()
                let uniqueLocal = (bytes[0] &&& 0xfeuy) = 0xfcuy

                IPAddress.IsLoopback(address)
                || address.IsIPv6LinkLocal
                || address.IsIPv6SiteLocal
                || address.IsIPv6Multicast
                || address.Equals(IPAddress.IPv6Any)
                || uniqueLocal
                || (bytes[0] = 0x20uy && bytes[1] = 0x01uy && bytes[2] = 0x0duy && bytes[3] = 0xb8uy)
        else
            let bytes = address.GetAddressBytes()
            let first, second = int bytes[0], int bytes[1]

            first = 0
            || first = 10
            || first = 127
            || first >= 224
            || (first = 169 && second = 254)
            || (first = 172 && second >= 16 && second <= 31)
            || (first = 192 && (second = 0 || second = 168))
            || (first = 100 && second >= 64 && second <= 127)
            || (first = 198 && (second = 18 || second = 19))

    let isSafeAsync (uri: Uri) (cancellationToken: CancellationToken) : Task<bool> =
        task {
            let validScheme = uri.Scheme = Uri.UriSchemeHttps || uri.Scheme = Uri.UriSchemeHttp
            let noCredentials = String.IsNullOrEmpty(uri.UserInfo)

            if not validScheme || not noCredentials then
                return false
            elif uri.IsLoopback || uri.DnsSafeHost.Equals("localhost", StringComparison.OrdinalIgnoreCase) then
                return false
            else
                let! addresses =
                    match IPAddress.TryParse(uri.DnsSafeHost) with
                    | true, address -> Task.FromResult([| address |])
                    | _ -> Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken)

                return addresses.Length > 0 && addresses |> Array.forall (isPrivateAddress >> not)
        }
