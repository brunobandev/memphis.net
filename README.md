<div align="center">

![Memphis light logo](https://github.com/memphisdev/memphis-broker/blob/master/logo-white.png?raw=true#gh-dark-mode-only)

</div>

<div align="center">

![Memphis light logo](https://github.com/memphisdev/memphis-broker/blob/master/logo-black.png?raw=true#gh-light-mode-only)

</div>

<div align="center">
<h4>Simple as RabbitMQ, Robust as Apache Kafka, and Perfect for microservices.</h4>

<img width="750" alt="Memphis UI" src="https://user-images.githubusercontent.com/70286779/204081372-186aae7b-a387-4253-83d1-b07dff69b3d0.png"><br>


<a href="https://landscape.cncf.io/?selected=memphis"><img width="200" alt="CNCF Silver Member" src="https://github.com/cncf/artwork/raw/master/other/cncf-member/silver/white/cncf-member-silver-white.svg#gh-dark-mode-only"></a>

</div>

<div align="center">

  <img width="200" alt="CNCF Silver Member" src="https://github.com/cncf/artwork/raw/master/other/cncf-member/silver/color/cncf-member-silver-color.svg#gh-light-mode-only">

</div>

 <p align="center">
  <a href="https://sandbox.memphis.dev/" target="_blank">Sandbox</a> - <a href="https://memphis.dev/docs/">Docs</a> - <a href="https://twitter.com/Memphis_Dev">Twitter</a> - <a href="https://www.youtube.com/channel/UCVdMDLCSxXOqtgrBaRUHKKg">YouTube</a><br>
  Created and maintained by the Memphis community.<br>Maintainer: @bazen-teklehaymanot
</p>

<p align="center">
<a href="https://discord.gg/WZpysvAeTf"><img src="https://img.shields.io/discord/963333392844328961?color=6557ff&label=discord" alt="Discord"></a> 
<a href=""><img src="https://img.shields.io/github/issues-closed/memphisdev/memphis-broker?color=6557ff"></a> 
<a href="https://github.com/memphisdev/memphis-broker/blob/master/CODE_OF_CONDUCT.md"><img src="https://img.shields.io/badge/Code%20of%20Conduct-v1.0-ff69b4.svg?color=ffc633" alt="Code Of Conduct"></a> 
<a href="https://github.com/memphisdev/memphis-broker/blob/master/LICENSE"><img src="https://img.shields.io/github/license/memphisdev/memphis-broker?color=ffc633"></a> <img alt="GitHub release (latest by date)" src="https://img.shields.io/github/v/release/memphisdev/memphis-broker?color=61dfc6"> <img src="https://img.shields.io/github/last-commit/memphisdev/memphis-broker?color=61dfc6&label=last%20commit">
</p>

**[Memphis](https://memphis.dev)** is a next-generation alternative to traditional message brokers.<br><br>
A simple, robust, and durable cloud-native message broker wrapped with<br>
an entire ecosystem that enables cost-effective, fast, and reliable development of modern queue-based use cases.<br><br>
Memphis enables the building of modern queue-based applications that require<br>
large volumes of streamed and enriched data, modern protocols, zero ops, rapid development,<br>
extreme cost reduction, and a significantly lower amount of dev time for data-oriented developers and data engineers.

## Installation

```sh
 dotnet add package Memphis.Client -v ${MEMPHIS_CLIENT_VERSION}
```

## Update

```sh
Update-Package Memphis.Client
```

## Importing

```c#
using Memphis.Client;
```

### Connecting to Memphis

First, we need to create or use default `ClientOptions` and then connect with Memphis by using `MemphisClientFactory.CreateClient(ClientOptions opst)`.

```c#
try
{
    var options = MemphisClientFactory.GetDefaultOptions();
    options.Host = "<broker-address>";
    options.Username = "<application-type-username>";
    options.ConnectionToken = "<token>";
    var memphisClient = MemphisClientFactory.CreateClient(options);
    ...
}
catch (Exception ex)
{
    Console.Error.WriteLine("Exception: " + ex.Message);
    Console.Error.WriteLine(ex);
}
```

Once client created, the entire functionalities offered by Memphis are available.

### Disconnecting from Memphis

To disconnect from Memphis, call `Dispose()` on the `MemphisClient`.

```c#
await memphisClient.Dispose()
```
### Creating a Station

```c#
try
{
    // First: creating Memphis client
    var options = MemphisClientFactory.GetDefaultOptions();
    options.Host = "<memphis-host>";
    options.Username = "<application type username>";
    options.ConnectionToken = "<broker-token>";
    var client = MemphisClientFactory.CreateClient(options);
    
    // Second: creaing Memphis station
    var station = await client.CreateStation(
        stationOptions: new StationOptions()
        {
            Name = "<station-name>",
            RetentionType = RetentionTypes.MAX_MESSAGE_AGE_SECONDS,
            RetentionValue = 604_800,
            StorageType = StorageTypes.DISK,
            Replicas = 1,
            IdempotencyWindowMs = 0,
            SendPoisonMessageToDls = true,
            SendSchemaFailedMessageToDls = true,
        });
}
catch (Exception ex)
{
    Console.Error.WriteLine("Exception: " + ex.Message);
    Console.Error.WriteLine(ex);
}
```

Memphis currently supports the following types of retention:

```c#
RetentionTypes.MAX_MESSAGE_AGE_SECONDS
```
The above means that every message persists for the value set in the retention value field (in seconds).

```c#
RetentionTypes.MESSAGES
```
The above means that after the maximum number of saved messages (set in retention value) has been reached, the oldest messages will be deleted.

```c#
RetentionTypes.BYTES
```
The above means that after maximum number of saved bytes (set in retention value) has been reached, the oldest messages will be deleted.

### Storage Types
Memphis currently supports the following types of messages storage:

```c#
StorageTypes.DISK
```
The above means that messages persist on disk.

```c#
StorageTypes.MEMORY
```
The above means that messages persist on the main memory.

### Destroying a Station

Destroying a station will remove all its resources (including producers and consumers).
```c#
station.DestroyAsync()
```

### Attaching a Schema to an Existing Station
```c#
await client.AttachSchema(stationName: "<station-name>", schemaName: "<schema-name>");
```

### Detaching a Schema from Station
```c#
await client.DetachSchema(stationName: station.Name);
```


### Produce and Consume messages

The most common client operations are `produce` to send messages and `consume` to
receive messages.

Messages are published to a station and consumed from it by creating a consumer.
Consumers are pull based and consume all the messages in a station unless you are using a consumers group, in this case messages are spread across all members in this group.

Memphis messages are payload agnostic. Payloads are `byte[]`.

In order to stop getting messages, you have to call `consumer.Dispose()`. Destroy will terminate regardless
of whether there are messages in flight for the client.

### Creating a Producer

```c#
try
{
   // First: creating Memphis client
    var options = MemphisClientFactory.GetDefaultOptions();
    options.Host = "<memphis-host>";
    options.Username = "<application type username>";
    options.ConnectionToken = "<broker-token>";
    var client = MemphisClientFactory.CreateClient(options);

    // Second: creating the Memphis producer 
    var producer = await client.CreateProducer(
        stationName: "<memphis-station-name>",
        producerName: "<memphis-producer-name>",
        generateRandomSuffix:true);    
}
catch (Exception ex)
{
    Console.Error.WriteLine("Exception: " + ex.Message);
    Console.Error.WriteLine(ex);
}
```

### Producing a message

```c#
var commonHeaders = new NameValueCollection();
commonHeaders.Add("key-1", "value-1");

await producer.ProduceAsync(Encoding.UTF8.GetBytes(text), commonHeaders);
```

### Destroying a Producer

```c#
await producer.DestroyAsync()
```

### Creating a Consumer

```c#
try
{
    // First: creating Memphis client
    var options = MemphisClientFactory.GetDefaultOptions();
    options.Host = "<memphis-host>";
    options.Username = "<application type username>";
    options.ConnectionToken = "<broker-token>";
    var client = MemphisClientFactory.CreateClient(options);
    
    // Second: creaing Memphis consumer
    var consumer = await client.CreateConsumer(new ConsumerOptions
    {
        StationName = "<station-name>",
        ConsumerName = "<consumer-name>",
        ConsumerGroup = "<consumer-group-name>",
    }); 
       
}
catch (Exception ex)
{
    Console.Error.WriteLine("Exception: " + ex.Message);
    Console.Error.WriteLine(ex);
}
```

### Creating message handler for consuming a message

First, create a callback functions that receives a args that holds list of MemhpisMessage.
Then, pass this callback into consumer.Consume function.
The consumer will try to fetch messages every _PullIntervalMs_ (that was given in Consumer's creation) and call the defined message handler.

```c#
EventHandler<MemphisMessageHandlerEventArgs> msgHandler = (sender, args) =>
{
    if (args.Exception != null)
    {
        Console.Error.WriteLine(args.Exception);
        return;
    }

    foreach (var msg in args.MessageList)
    {
        //print message itself
        Console.WriteLine("Received data: " + Encoding.UTF8.GetString(msg.GetData()));


        // print message headers
        foreach (var headerKey in msg.GetHeaders().Keys)
        {
            Console.WriteLine(
                $"Header Key: {headerKey}, value: {msg.GetHeaders()[headerKey.ToString()]}");
        }

        Console.WriteLine("---------");
        msg.Ack();
    }
};
```

### Consuming a message

```c#
 await consumer.ConsumeAsync( msgCallbackHandler:msgHandler, dlqCallbackHandler:msgHandler);
```

### Acknowledging a Message

Acknowledging a message indicates to the Memphis server to not re-send the same message again to the same consumer or consumers group.

```c#
msg.Ack();
```

### Get headers

Get headers per message

```c#
msg.GetHeaders()
```

### Destroying a Consumer

```c#
await consumer.DestroyAsync();
```
