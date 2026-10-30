namespace OpmlSyncNewsBlur

open System
open System.Security.Cryptography
open System.Text

module Signer =
    let verify (secret: string) (body: byte array) (signature: string) =
        let expected =
            use hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret))
            "sha256=" + (hmac.ComputeHash(body) |> Convert.ToHexString |> fun value -> value.ToLowerInvariant())

        let expectedBytes = Encoding.UTF8.GetBytes(expected)
        let actualBytes = Encoding.UTF8.GetBytes(signature)

        expectedBytes.Length = actualBytes.Length
        && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes)

    let randomSecret () =
        RandomNumberGenerator.GetBytes(32) |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()
