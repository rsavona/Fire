using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using System.Threading.Tasks;
using DeviceSpace.Common;
using DeviceSpace.Common.Contracts;


namespace Device.Api.Suite;

[ApiController]
[Route("api/[controller]")]
public abstract class GenericController<TEntity> : ControllerBase where TEntity : class, IEntity
{
    private readonly IRepository<TEntity> _repository;
    private readonly IMessageBus _messageBus; // Inject the bus

    public GenericController(IRepository<TEntity> repository, IMessageBus messageBus)
    {
        _repository = repository;
        _messageBus = messageBus;
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<TEntity>> GetById(int id)
    {
        var item = await _repository.GetByIdAsync(id);
        if (item == null) return NotFound();

        return Ok(item);
    }

    [HttpPost]
    public async Task<ActionResult<TEntity>> Create(TEntity entity)
    {
        // 1. Save to the database
        var newId = await _repository.CreateAsync(entity);
        entity.Id = newId;
       
        // 2. Broadcast to the WCS!
        string jsonPayload = System.Text.Json.JsonSerializer.Serialize(entity);
        var msg = new MessageEnvelope(new MessageBusTopic("webb.API"), jsonPayload);
        await _messageBus.PublishAsync("webb.API", msg);

        return CreatedAtAction(nameof(GetById), new { id = entity.Id }, entity);
    }
}