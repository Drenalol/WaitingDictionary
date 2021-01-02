# WaitingDictionary
 Represents a thread-safe collection of keys and ```TaskCompletionSource{T}``` as values, managed with two methods: ```WaitAsync, SetAsync```.
 
 ![Image of Waiting Guy](https://cs9.pikabu.ru/post_img/big/2017/01/28/7/1485602875186056892.jpg)
 
### Usage
#### WaitAsync, SetAsync
Just mock model
```c#
public class Mock
{
    public List<Mock> Nodes { get; }
    
    public Mock(params Mock[] mock)
    {
        (Nodes ??= new List<Mock>()).AddRange(mock);
    }
    
    public Mock()
    {
    }
}
```
Base example
```c#
var dict = new WaitingDictionary<int, Mock>();
// Starting waiting Task<Mock> (blocking until completed or cancelled) by key 1337
var result = await dict.WaitAsync(1337, CancellationToken.None);
//
//
// Some operations on other threads, or tasks..
//
//
// Set result by key 1337, it will released previously task got it by WaitAsync
await dict.SetAsync(1337, new Mock());
```
Another example
```c#
var dict = new WaitingDictionary<int, Mock>();
// Set result by key 1337
await dict.SetAsync(1337, new Mock());
//
//
// Some operations on other threads, or tasks..
//
//
// Got result immideatly
var result = await dict.WaitAsync(1337, CancellationToken.None);
```
#### Behind the scenes
```SetAsync and WaitAsync automatically removes elements when it is completed.```
#### (Optionally) MiddlewareBuilder
```c#
var middlewares =
    new MiddlewareBuilder<Mock>()
        // Will run on every completion SetAsync
        // Default: none
        .RegisterCompletionActionInSet(() => Console.WriteLine("Set completed"))
        // Will run on every completion WaitAsync
        // Default: none
        .RegisterCompletionActionInWait(() => Console.WriteLine("Wait completed"))
        // Will run on every duplication element found while executed SetAsync
        // Default: throw exception
        .RegisterDuplicateActionInSet((old, @new) => new Mock(old, @new)) // merge two values
        // Will run on every cancellation WaitAsync by token
        // Default: TrySetCanceled
        .RegisterCancellationActionInWait((tcs, hasOwnToken) => tcs.SetException(new Exception("Something went wrong")));

var dict = new WaitingDictionary<int, Mock>(middlewares);
```
