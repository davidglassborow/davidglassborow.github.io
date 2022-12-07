---
layout: post
title: "Answering the phone, functionally"
date: 2017-12-21 06:30:00 +0100
comments: true
author: David 
---

# F# workflow for working with Twilio

This post is part of the [2017 F# Advent calendar](https://sergeytihon.com/2017/10/22/f-advent-calendar-in-english-2017/).

Lammy the elf is frustrated.  He's constantly interupted by the phone when trying to get on with his work,
and Santa isn't the most tolerent this time of year, there is a **lot** to be done.

![happy elf](/files/elf.jpg)

As well as being a semi-magical creature and full time toy maker, Lammy likes to code in his spare time.
He's been using C# for years, and in the past has used [Twilio](https://www.twilio.com). 

## A brief introduction to Twilio

Twilio is a hosted service where you rent a telephone number from them, and when somebody calls (or txts) the number, the Twilio servers
call an API you
provide, and the Twilio software will do what you tell it to do (e.g. hangup, forward to another number, take a message, etc).

- [What is Twilio and How Does the Twilio API Work?](https://www.twilio.com/learn/twilio-101/what-is-twilio)

Basically a phone number is setup with Twilio. When that number is called, the Twilio server calls a configured API via an HTTP POST
with various form variables set like the Call ID, which number is calling, etc.

The API returns TwiML (which is their XML based language for controlling the call) to tell the server what to do, and it carries it out.

For example, the following sequence shows a new call to a number, followed by an API requesting a message is said, 2 digits are captured,
and then the call is hung up:

![sequence](/files/sequence.svg)

The say and gather TwiML looks like:

```xml
<Response>
  <Say>Please type in your pin number to proceed</Say>
  <Gather input="dtmf" numDigits="2"></Gather>
</Response>
```

This is how a number is configured on the Twilio website, a webhook is configured to make a POST to a given URL.

![config](/files/api_setup.png)

Twilio can do lots of other things as well, including outbound calls, conferencing, calls from the browser and mobile apps, but 
Lammy has decided he just wants to implement something simple to save himself time.

![twilio_summary](/files/twilio.png)

## The challenge

Lammy decides a Twilio phone system can automate answering the phone, leaving him more time to build toys for the world's children.

He sits down with an [online  tool](http://flowchart.js.org/) and quickly whips up a flowchart for the flow he wants
to implement.  He ends up with 3 different things he wants to happen during a call, depending on when the call is, and what the caller would like:

1. Hangup after reading a message that the North Pole is shut.
2. Take a message and email to the customer service team for them to followup at a later date.
3. Forward the call to one of the 3 customer service departments (outsourced), run out of the South Pole.

![flowchart](/files/flow.png)

## Workflows

Last time Lammy worked with Twilio, he used C#. It worked but he ended up with lots of different functions, and different
API endpoints, and he struggled to understand the logic a week later when reading the code.

An example of some old C# code is below, with lots of different actions in an MVC site, the logic of the call is unclear:

```csharp

public IActionResult Chosen_Department(string Digits)
{
    if (Digits == "1" || Digits == "2" || Digits == "3")
        return Logic.TakeMessageAndEmail().xml();
    if (Digits == "6")
        return Logic.GetPIN().xml();
    // Anything else go back to start
    return Logic.GobackToStart().xml();
}

public IActionResult Pin_entered(string Digits)
{
    if (Digits == "321")
        return Logic.CallConference( callbackForConference() ).xml();
    return Logic.GobackToStart().xml();
}

public IActionResult Chosen_Person(string Digits)
{
    switch (Digits)
    {
        case "1": return Logic.PhoneElf1().xml();
        case "2": return Logic.PhoneElf2().xml();
        default:
                  return Logic.ListPeople().xml();
    }
}

// With an example of the logic calls being:

public static TwiML ListPeople() => 
    new VoiceResponse().Append(
        new Gather(numDigits: 1, action: action("chosen_person"), method: HttpMethod.Get).
            Play(sample("TalkToElf1.wav")).
            Play(sample("TalkToElf2.wav"))).
        Redirect(action("new"), method: HttpMethod.Get);
```


Although he still enjoys C#, he had become increasingly interested in F# over the last couple of years. He's been faithfully
reading [F# for fun and profit](https://fsharpforfunandprofit.com), and in particular he has recently been reading about an
advanced F# feature called [Computation Expressions](https://docs.microsoft.com/en-us/dotnet/fsharp/language-reference/computation-expressions).

He knows from a local talk at the NPHUG (North Pole Haskell Usergroup) that `Computation Expressions` implement syntactic
sugar for `Monoids`, `Monads`, `MonadPlus` and maybe a couple of others, but he doesn't really know what that means. He does
know that its allows him to interupt the normal flow of statments, so he quickly reads the [Computation Expression Series](https://fsharpforfunandprofit.com/series/computation-expressions.html).


## Starting off

His first step is to model what he want's the Twilio server to do for him. He quickly defines a union type:

```fsharp
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
```

The next step is to think how to model the flow of information.
The easiest situation is when a single message is returned and then the call is complete.

```fsharp
type CallHandlingProgram =
| Complete of result:PhoneCommand
```

Given this, he builds a very basic computation expression (or workflow as he prefers to call it), that
can return a single message to the Twilio call. It has a single definition, Return, which describes how to
wrap up the return.  In this case its is wrapped in the `CallHandlingProgram.Complete` case.

```fsharp
type CallBuilder() =
    // No Zero method - we must return something
    member this.Return(a:PhoneCommand) = Complete a
let call = new CallBuilder()
```

With this in place, the first couple of steps can be written.
Note at this stage, he is just returning the PhoneCommand cases defined above. Later they will be converted into XML.

```fsharp
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
                return Hangup "Sorry, the rest of the program hasn't been written yet"
    }
```

Lammy is happy with this, he can write a nice DSL that describes what he wants to do, but it hasn't given him anything he couldn't
do in C#.  What about the interaction ? how to get the user to press keys, and his workflow get called back to continue on ?

He realises he needs to extend his concept of a CallHandlingProgram.

He knows that ultimately Twilio will be calling back over HTTP, so the input is going to be some sort of string.
So he represents the callback by extending the `CallHandlingProgram` - adding another case, which is where the
workflow is in progress, waiting for more information from the Twilio server.

```fsharp
type CallHandlingProgram =
| Complete of result:PhoneCommand
| InProgress of result:PhoneCommand * nextStep: (string -> CallHandlingProgram)
```

The `InProgress` case captures the command to send back to the server, **and** the function to carry on with once an answer arrives.

## Using bind to handle inputs

The `Bind` call allows the behaviour of the workflow to be altered. Given:

```fsharp
    let! something = producer()
    ...
```

The compiler will convert it into a call something like
```fsharp
    CallBuilder.Bind( producer(), fun something -> ... )
```

This gives Lammy the opportunity to suspend the computation, send the response to Twilio, and run it later when Twilio calls back.
He creates a new type to capture that more information is needed.  The `bind` uses this to wrap the response now, and the future callback
into the new InProgress case. He adds a helper to wrap the `WaitForKeypresses` into `NeedsKey`.

```fsharp

/// Use internally for any step where we need to wait for a string
type Internal =
| NeedKey of PhoneCommand

type CallBuilder() =
    // No zero - we must return something
    member this.Return(a:PhoneCommand) = Complete a
    member this.Bind(NeedKey(x),f) =
            InProgress(x, fun x -> f(x) )

let waitForKeypress msg num = 
    WaitForKeypresses(msg,num) |> NeedKey

```

This is all that is needed to write code like:

```fsharp
call {   
        let! keyPress = waitForKeypress "Please press 1 to discuss naughty lists, press 2 to discuss a reindeer malfunction, press 3 for any other enquires" 1
        match keyPress with
        | "1" -> return ConnectToQueue "Naughty children"
        | "2" -> return ConnectToQueue "Naughty raindeer"
        | "3" -> return ConnectToQueue "Account enquires"
}
```

This will define a call program that returns WaitForKeypress message back to Twilio, and wrap up the match code in a future
function, all put into a `InProgress` wrapper.

There is one last simple step required. The flowchart needs to repeatadly read a message to the user and wait for a key press, so looping is needed.
In a functional program, this often means recursion. By adding a `ReturnFrom` method to the builder, the `call` workflow can call itself using `return!`:

```fsharp
type CallBuilder() =
    // No zero - we must return something
    member this.Return(a:PhoneCommand) = Complete a
    member this.ReturnFrom(a) = a
    member this.Bind(NeedKey(x),f) =
            InProgress(x, fun x -> f(x) )

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
```

Note the addition of a counter so the code can give up after a certain number of times asking the caller to press a key.

## Interpreting the CallHandlingProgram

The `call` computation expression will now build everything that is needed, a recursive `CallHandlingProgram` that describes
the process of handling a call. What is needed now is to *run* his final call handling program:

```fsharp
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
                // Strictly here we should repeadly read the message using recursion, but for conciseness just hangup
                | _   -> return Hangup "Happy christmas!"   
```

Lammy whips out a quick test, writing a small harness that takes a call program, and a list of key presses, and runs the
workflow, listing out progress and what is returns at each step:

```fsharp
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
```

Lammy tries out the different scenarios to make sure everything is working as expected:

```fsharp
takeCall "123" "456" (DateTime(2017,12,24)) |> runIt [ ]
// Complete: Hangup "I'm sorry, we are now shut for the rest of year, happy holidays !"

takeCall "123" "456" (DateTime(2017,12,22,17,0,0)) |> runIt [ ]
// Complete: TakeMessage "I'm sorry, we are closed for the day, please leave a message and we'll get back to you asap"

takeCall "123" "456" (DateTime(2017,12,22,12,0,0)) |> runIt [ "2" ]
// Stepped: WaitForKeypresses ("Please press 1 to talk to one of our elves, or 2 to leave us a message",1)
// key = 2
// Complete: TakeMessage "Please leave your message after the beep"

takeCall "123" "456" (DateTime(2017,12,22,12,0,0)) |> runIt [ "1"; "4"; "2" ]
// Stepped: WaitForKeypresses ("Please press 1 to talk to one of our elves, or 2 to leave us a message",1)
// key = 1
// Stepped: WaitForKeypresses ("Please press 1 to discuss naughty lists, press 2 to discuss a reindeer malfunction, press 3 for any other enquires", 1)
// key = 4
// Stepped: WaitForKeypresses ("Please press 1 to discuss naughty lists, press 2 to discuss a reindeer malfunction, press 3 for any other enquires", 1)
// key = 2
// Complete: ConnectToQueue "Naughty raindeer"
```

## Hooking up to the real server

Lammy is now very happy, he just needs to plumb the workflow logic into a server, ready to be called by Twilio.

The twilio server expects XML, so the first job is to map from nice F# types to yucky OO XML objects, using the Twilio 
nuget pacakge, and reading the [TwiML docs](https://www.twilio.com/docs/api/twiml/twilio_request) gives:

```fsharp
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
```

So for example

```fsharp
Hangup "I'm sorry, we are now shut for the rest of year, happy holidays !"
```

would be converted to the following xml:

```xml
<Response>
  <Say>I'm sorry, we are now shut for the rest of year, happy holidays !</Say>
  <Hangup></Hangup>
</Response>
```

The last step is to run the logic and store the state. Feeling a bit dirty (it is Christmas after all), he uses a mutable Map
that tracks each incoming call (by its `CallSid`, the unique id that Twilio for each call), that holds the callbacks when needed.

He defines a simple `handleCall` that takes a url (in case any of the TwiML commands need to set a different return URL),
an email address (needed for taking a message), and a dictionary of key:value pairs. He can then plug this into different server
technologies like [Suave](https://suave.io), [Giraffe](https://github.com/giraffe-fsharp/Giraffe), or any C# webserver.

```fsharp
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
```

Lammy happens to have an existing Asp.net core 2 C# site, so he adds the F# as a class library, and uses:

```csharp
public IActionResult Elf()
{
    var details = Request.Method == "GET" ?
        Request.Query.ToDictionary(a => a.Key.ToUpper(), a => a.Value.First()) :
        Request.Form.ToDictionary(a => a.Key.ToUpper(), a => a.Value.First());
    var url = String.Format("{0}://{1}{2}", Request.Scheme, Request.Host, Request.Path);
    return Workflow.handleCall(url, "lammy@TheNorthPole.mil", details).xml();
}
```

He configures the Twilio number to send webhook calls to the `Elf` action url, and he's away !

![elf cheer](/files/elf_cheer.jpg)

One thing the Lammy worries about, is what happens if a call is half way through and Twilio never calls back. Isn't he
going to be left with a half done workflow, taking up memory. 

He decides that a simple solution is a check once every minute
and clear up any old workflows that haven't been called in the last 5 minutes (the `callsInProgress` Map already tracks when the workflow was last run ).

He plans to write this, and get rid of the mutable state by putting it in an Agent, but runs out of time due to his toy making workload (nothing to do with the Eggnog I can assure you).

## Discussion

There are lots of ways Lammy could extend this approach:

- He could use an Agent to host each individual call, and the receive with a timeout feature would be ideal for clearing up.
- He could extend the different things we can ask Twilio to do by adding to the `PhoneCommand` type.
- He could make the callbacks more type safe, representing other situations like timeouts etc. within the type system.
- He could write a similar workflow for SMS message handling

Within the workflow itself, he could:

- Ask the caller to enter a security number and lookup customer info from a database based on the number they are calling from, 
validating the caller with the security number
- Use outgoing calls to Twilio to check how many people are currently waiting in queues, and take a message if they are too busy

etc.

Writing workflows can be difficult to get your head around at first (it was for Lammy), but they are worth it, allowing you to write
complex logic in a simple way, without needing to wait for the compiler team (e.g. async await in C#).

The full code can be found [on github](https://gist.github.com/davidglassborow/4f56ab18162a7e9ab3ebb246c1394c7f) or [here](/files/elf.fsx).

Happy Christmas !


![](/files/santa-on-phone.gif)

## References and more information

- [Microsoft Computation Expression docs](https://docs.microsoft.com/en-us/dotnet/fsharp/language-reference/computation-expressions)
- [F# for fun and profit - computation expression series](https://fsharpforfunandprofit.com/series/computation-expressions.html)
- [Computation Zoo Poster](http://tomasp.net/academic/papers/computation-zoo/poster-tfp.pdf)
- [The F# Computation Expression Zoo paper by Tomas and Don](http://tomasp.net/academic/papers/computation-zoo/computation-zoo.pdf)
- [FSharp Spec](http://fsharp.org/specs/language-spec/4.0/FSharpSpec-4.0-latest.pdf), section 6.3.10 - Computation Expressions
- [Stackoverflow: How does continuation monad really work](https://stackoverflow.com/questions/40052256/how-does-continuation-monad-really-work)
- [The Mother of all Monads](http://blog.sigfpe.com/2008/12/mother-of-all-monads.html)
- [The Continuation Monad](http://www.haskellforall.com/2012/12/the-continuation-monad.html)
