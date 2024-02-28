open System
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json

type Session = {
    id : string 
    title : string 
    description : string 
}

type LoginBody = {
    email : string 
    key : string
}

type CheckBody = {
    text : string 
    sandbox : bool
}

let loginAsync (client : HttpClient) (email : string) (key : string) = 
    async {
        let url = "https://id.copyleaks.com/v3/account/login/api"
        let loginBody: LoginBody = {
            email = email 
            key = key
        }
        let jsonString = JsonSerializer.Serialize(loginBody);
        let content : StringContent = new StringContent(jsonString, Encoding.UTF8, "application/json")
        
        let! response = client.PostAsync(url, content) |> Async.AwaitTask
        let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask
        let result = 
            match response.IsSuccessStatusCode with
            | true -> Ok body
            | false -> Error body
        return result
    }   

let getFilename sandbox scanId session = 
    let prefix = if sandbox then "sandbox" else "copyleaks"
    sprintf "%s-%s-%s.json" prefix scanId session.id

let checkAsync (client : HttpClient) (scanId : string) (token : string) (sandbox : bool) (text : string) =
    async {
        let url = sprintf "https://api.copyleaks.com/v2/writer-detector/%s/check" scanId
        let postBody = {
            text = text 
            sandbox = sandbox
        }
        let jsonString = JsonSerializer.Serialize(postBody);
        let content : StringContent = new StringContent(jsonString, Encoding.UTF8, "application/json")
        
        let requestMessage = new HttpRequestMessage(HttpMethod.Post, url);
        requestMessage.Content <- content
        requestMessage.Headers.Add("Authorization", sprintf "Bearer %s" token)

        let! response = client.SendAsync(requestMessage) |> Async.AwaitTask
        let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask
        let result = 
            match response.IsSuccessStatusCode with
            | true -> Ok body
            | false -> 
                printfn "%A" response
                Error body
        return result
    }

let checkSession (client : HttpClient) (scanId : string) (token : string) (sandbox : bool) (session : Session) : unit = 
    let filename = getFilename sandbox scanId session 
    if File.Exists(filename) then 
        printfn "Skipping session %s -> Found report %s." session.id filename
    else 
        let text = session.title + Environment.NewLine + Environment.NewLine + session.description 
        let checkResult = checkAsync client scanId token sandbox text |> Async.RunSynchronously
        match checkResult with 
        | Ok body -> 
            File.WriteAllText(filename, body)
            printfn "Checked session %s -> Wrote report %s" session.id filename
        | Error body -> 
            printfn "!!! Failed to check session %s: %s" session.id body 

let checkSessions (client : HttpClient) (scanId : string) (token : string) (sandbox : bool) (sessions : Session list) = 
    sessions |> List.iter (checkSession client scanId token sandbox)

let runCheck (email : string) (scanId : string) (apikey : string) (sandbox : bool) (sessions : Session list) = 
    use httpClient = new HttpClient() 
    let loginResult = 
        loginAsync httpClient email apikey
        |> Async.RunSynchronously
    match loginResult with 
    | Ok body -> 
        printfn "Login succeeded."
        let jsonDoc = JsonDocument.Parse(body)
        let token = jsonDoc.RootElement.GetProperty("access_token").GetString()
        sessions |> checkSessions httpClient scanId token sandbox 
    | Error body -> 
        printfn "Login failed: %s" body 

let toSessionRecord (sessionElement : JsonElement) = 
    let getString (prop : string) = sessionElement.GetProperty(prop).GetString()
    { id = getString "id"
      title = getString "title"
      description = getString "description" }

let parseInput (inputfile : string) = 
    let text = File.ReadAllText(inputfile)
    let jsonDoc = JsonDocument.Parse(text)
    let sessions = 
        jsonDoc.RootElement.GetProperty("sessions").EnumerateArray()
        |> Seq.toList
        |> List.map toSessionRecord 
    sessions

[<EntryPoint>]
let main argv =
    let inputfile = argv[0]
    let email = argv[1]
    let apikey = argv[2]
    let scanId = argv[3]
    let sandbox = argv[4] <> "copyleaks"
    printfn "inputfile = %s" inputfile 
    printfn "email = %s" email 
    printfn "apikey = %s" apikey 
    printfn "scanId = %s" scanId 
    printfn "sandbox = %b" sandbox
    let sessions = parseInput inputfile
    sessions |> runCheck email scanId apikey sandbox
    0
