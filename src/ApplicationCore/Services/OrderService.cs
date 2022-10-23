using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Ardalis.GuardClauses;
using Azure.Messaging.ServiceBus;
using Microsoft.eShopWeb.ApplicationCore.Entities;
using Microsoft.eShopWeb.ApplicationCore.Entities.BasketAggregate;
using Microsoft.eShopWeb.ApplicationCore.Entities.OrderAggregate;
using Microsoft.eShopWeb.ApplicationCore.Interfaces;
using Microsoft.eShopWeb.ApplicationCore.Specifications;
using Newtonsoft.Json;

namespace Microsoft.eShopWeb.ApplicationCore.Services;

public class OrderService : IOrderService
{
    private readonly IRepository<Order> _orderRepository;
    private readonly IUriComposer _uriComposer;
    private readonly IRepository<Basket> _basketRepository;
    private readonly IRepository<CatalogItem> _itemRepository;

    public OrderService(IRepository<Basket> basketRepository,
        IRepository<CatalogItem> itemRepository,
        IRepository<Order> orderRepository,
        IUriComposer uriComposer)
    {
        _orderRepository = orderRepository;
        _uriComposer = uriComposer;
        _basketRepository = basketRepository;
        _itemRepository = itemRepository;
    }

    public async Task CreateOrderAsync(int basketId, Address shippingAddress)
    {
        var basketSpec = new BasketWithItemsSpecification(basketId);
        var basket = await _basketRepository.GetBySpecAsync(basketSpec);

        Guard.Against.NullBasket(basketId, basket);
        Guard.Against.EmptyBasketOnCheckout(basket.Items);

        var catalogItemsSpecification = new CatalogItemsSpecification(basket.Items.Select(item => item.CatalogItemId).ToArray());
        var catalogItems = await _itemRepository.ListAsync(catalogItemsSpecification);

        var items = basket.Items.Select(basketItem =>
        {
            var catalogItem = catalogItems.First(c => c.Id == basketItem.CatalogItemId);
            var itemOrdered = new CatalogItemOrdered(catalogItem.Id, catalogItem.Name, _uriComposer.ComposePicUri(catalogItem.PictureUri));
            var orderItem = new OrderItem(itemOrdered, basketItem.UnitPrice, basketItem.Quantity);
            return orderItem;
        }).ToList();

        var order = new Order(basket.BuyerId, shippingAddress, items);

        await SendToBlobStorageAsync(order);
        
        await _orderRepository.AddAsync(order);
    }

    private async Task SendToBlobStorageAsync(Order order)
    {
        var data = JsonConvert.SerializeObject(order, Formatting.Indented);

        var constring = Environment.GetEnvironmentVariable("ServiceBus");
        var qname = Environment.GetEnvironmentVariable("Qname");
        var email = Environment.GetEnvironmentVariable("ErrorEmail");
        await using var client = new ServiceBusClient(constring, new ServiceBusClientOptions() 
        { 
            RetryOptions = new ServiceBusRetryOptions() 
            { 
                MaxRetries = 3
            } 
        });
        await client.CreateSender(qname).SendMessageAsync(new ServiceBusMessage(data));

        ServiceBusProcessor _ordersProcessor = client.CreateProcessor(qname);
        _ordersProcessor.ProcessMessageAsync += async(ProcessMessageEventArgs arg) =>
        {
            return Task.CompletedTask;
        };
        _ordersProcessor.ProcessErrorAsync += async (ProcessErrorEventArgs arg) =>
        {
            var client = new HttpClient();
            await client.PostAsync(email, new StringContent(data));
        };
        await _ordersProcessor.StartProcessingAsync();
    }
}
