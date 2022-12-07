#r "packages/Twilio/lib/net451/Twilio.dll"
#r "System.Xml.Linq"
open System
open Twilio.TwiML
open Twilio.TwiML.Voice

module Model =

    /// Represents what we will tell the Twilio phone system to do
    type PhoneCommand =
    /// Connect the caller to a given phone queue
    | ConnectToQueue of queueName:string
    /// Say the message, and then hangup
    | Hangup of msg:string
    /// Say the message, then record the caller's message
    | TakeMessage of msg:string
    /// Say the message, then wait for a user to input a given number of key presses
    | WaitForKeypresses of msg:string * numberDigits:int

open Model

module TwilioResults =

    /// Serialize the TwiML objects to XML string
    let toXml (r:#TwiML) = r.ToString(System.Xml.Linq.SaveOptions.None)

    /// Convert our internal commands into the XML that twilio expects
    let commandToXML url email cmd = 
        let say msg = VoiceResponse().Append(Say(msg))

        match cmd with

        | ConnectToQueue(queueName) ->
            // https://www.twilio.com/docs/api/twiml/dial
            // Assume: conferences are used for call queues
            VoiceResponse().Append( Dial().Append(Conference(queueName)))

        | Hangup(msg) ->
            // https://www.twilio.com/docs/api/twiml/hangup
            say(msg).Append(Twilio.TwiML.Voice.Hangup())

        | TakeMessage(msg) ->
            // https://www.twilio.com/labs/twimlets/voicemail
            say(msg).Append(Redirect( Uri( sprintf "http://twimlets.com/voicemail?Transcribe=false&Message=''&Email=%s" email )))

        | WaitForKeypresses(msg, numberDigits) ->
            // https://www.twilio.com/docs/api/twiml/gather
            say(msg).Append(Gather(input = Gather.InputEnum.Dtmf, numDigits = Nullable.op_Implicit(numberDigits)))




/// What our workflow returns
type CallHandlingProgram =
| Complete of result:PhoneCommand
| InProgress of result:PhoneCommand * nextStep: (string -> CallHandlingProgram)

/// Use internally for any step where we need to wait for a string
type Internal =
| NeedKey of PhoneCommand

module CallHandling =

    type CallBuilder() =
        // No zero - we must return something
        member this.Return(a:PhoneCommand) = Complete a
        member this.ReturnFrom(a) = a
        member this.Bind(NeedKey(x),f) =
                InProgress(x, fun x -> f(x) )
    let call = new CallBuilder()

open CallHandling

let waitForKeypress msg num = 
    WaitForKeypresses(msg,num) |> NeedKey

let listQueuesAndWaitForResponse() =
    // We make this recursive so we can try a number of times before bugging out
    let rec handleQueues retries = call {   
        let! keyPress = waitForKeypress "Please press 1 to discuss naughty lists, press 2 to discuss a reindeer malfunction, press 3 for any other enquires" 1
        match keyPress with
        | "1" -> return ConnectToQueue "Naughty children"
        | "2" -> return ConnectToQueue "Naughty raindeer"
        | "3" -> return ConnectToQueue "Account enquires"
        | _   -> 
            match retries with            
            | i when i >= 3 -> return Hangup "Sorry, key not recognised"
            | _             -> return! handleQueues (retries+1)
    }
    handleQueues 0


let takeCall fromNumber toNumber (now:System.DateTime) =
    call {
        // 1. If the call is on Christmas Eve or later, leave a message saying we are busy
        if now.DayOfYear >= 358 then        // 358 is the 24th of December
            return Hangup "I'm sorry, we are now shut for the rest of year, happy holidays !"
        else

            // 2. If the call is out of hours, request a message is left and email it to ourselves
            // Elves are 9 to 5 workers
            if now.Hour <= 8 || now.Hour >= 17 then
                return TakeMessage "I'm sorry, we are closed for the day, please leave a message and we'll get back to you asap"
            else

                // 3. Offer to talk to elves, or take a mesasge
                let! keyPress = waitForKeypress "Please press 1 to talk to one of our elves, or 2 to leave us a message" 1
                match keyPress with                
                | "1" -> return! listQueuesAndWaitForResponse()
                | "2" -> return TakeMessage "Please leave your message after the beep"
                | _   -> return Hangup "Happy christmas!"
    }

let rec runIt listOfKeyPresses csr =
    match csr with
    | Complete(x)        -> 
        printfn "Complete: %A" x
    | InProgress(x,next) -> 
        printfn "Stepped: %A" x
        match listOfKeyPresses with
        | [] -> 
            failwith "Run out of input keys"
        | keysPressed::futureKeys ->
            printfn "key = %s" keysPressed
            runIt futureKeys (next keysPressed)


//takeCall "123" "456" (DateTime(2017,12,24)) |> runIt [ ]
//takeCall "123" "456" (DateTime(2017,12,22,17,0,0)) |> runIt [ ]
//takeCall "123" "456" (DateTime(2017,12,22,12,0,0)) |> runIt [ "2" ]
//takeCall "123" "456" (DateTime(2017,12,22,12,0,0)) |> runIt [ "1"; "4"; "4"; "2" ]

module Controller = 
    open System.Collections.Generic

    type Callback = DateTime * (string -> CallHandlingProgram)
    let mutable callsInProgress: Map<string,Callback> = Map.empty

    let removeFromMap callsid = 
        fun _ -> callsInProgress <- callsInProgress.Remove(callsid) 
        |> lock callsInProgress 

    let addToMap callsid f =
        fun _ -> callsInProgress <- callsInProgress.Add(callsid,(DateTime.Now,f))  
        |> lock callsInProgress 

    let stepProgram callid inMap step = 
        match step with
        | Complete(response)->
            if inMap then removeFromMap callid
            response
        | InProgress(response,f) ->
            addToMap callid f
            response

    let handleCall (url:string) (email:string) (parms:Dictionary<string,string>) : VoiceResponse =

        let callid = parms.["CALLSID"]

        match callsInProgress.TryFind callid with
        | None ->
            
            // This is a new call, lets get the workflow to run
            let program = takeCall parms.["FROM"] parms.["TO"] System.DateTime.Now
            stepProgram callid false program

        | Some (_,program) ->
            
            // This is a program waiting for a callback, so run it with the digits entered
            let result = program parms.["DIGITS"]
            stepProgram callid true result

        |> TwilioResults.commandToXML url email
