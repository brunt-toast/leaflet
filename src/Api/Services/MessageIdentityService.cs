using System.Security.Cryptography;
using Api.Entities;
using AppDbContext = Api.Context.AppContext;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

internal sealed class MessageIdentityService(AppDbContext appContext) : IMessageIdentityService
{
    private const int SingletonInstanceStateId = 1;
    private const int InstanceDiscriminatorBits = 32;
    private const long InstanceDiscriminatorMask = (1L << InstanceDiscriminatorBits) - 1L;
    private const long MaxLogicalTime = long.MaxValue >> InstanceDiscriminatorBits;
    private static readonly SemaphoreSlim Gate = new(initialCount: 1, maxCount: 1);

    public async Task<long> CreateMessageIdAsync(string roomHash, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);

        try
        {
            uint instanceDiscriminator = await GetOrCreateInstanceDiscriminatorAsync(cancellationToken);
            RoomClockEntity roomClock = await GetOrCreateRoomClockAsync(roomHash, cancellationToken);

            if (roomClock.LastLogicalTime >= MaxLogicalTime)
            {
                throw new InvalidOperationException($"Room '{roomHash}' exhausted its logical clock range.");
            }

            roomClock.LastLogicalTime++;
            await appContext.SaveChangesAsync(cancellationToken);

            return ComposeMessageId(roomClock.LastLogicalTime, instanceDiscriminator);
        }
        finally
        {
            Gate.Release();
        }
    }

    public Task ObserveMessageIdAsync(string roomHash, long messageId, CancellationToken cancellationToken)
    {
        return ObserveMessageIdsAsync([(roomHash, messageId)], cancellationToken);
    }

    public async Task ObserveMessageIdsAsync(IEnumerable<(string RoomHash, long MessageId)> observedMessages, CancellationToken cancellationToken)
    {
        (string RoomHash, long MessageId)[] observations = observedMessages.ToArray();
        if (observations.Length == 0)
        {
            return;
        }

        await Gate.WaitAsync(cancellationToken);

        try
        {
            foreach (IGrouping<string, (string RoomHash, long MessageId)> roomGroup in observations.GroupBy(observation => observation.RoomHash))
            {
                long observedLogicalTime = roomGroup.Max(observation => ExtractLogicalTime(observation.MessageId));
                RoomClockEntity roomClock = await GetOrCreateRoomClockAsync(roomGroup.Key, cancellationToken);

                if (observedLogicalTime > roomClock.LastLogicalTime)
                {
                    roomClock.LastLogicalTime = observedLogicalTime;
                }
            }

            await appContext.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<uint> GetOrCreateInstanceDiscriminatorAsync(CancellationToken cancellationToken)
    {
        InstanceStateEntity? instanceState = await appContext.InstanceStates
            .SingleOrDefaultAsync(state => state.Id == SingletonInstanceStateId, cancellationToken);

        if (instanceState is not null)
        {
            return instanceState.InstanceDiscriminator;
        }

        instanceState = new InstanceStateEntity
        {
            Id = SingletonInstanceStateId,
            InstanceDiscriminator = unchecked((uint)RandomNumberGenerator.GetInt32(int.MinValue, int.MaxValue)),
        };

        if (instanceState.InstanceDiscriminator == 0)
        {
            instanceState.InstanceDiscriminator = 1;
        }

        await appContext.InstanceStates.AddAsync(instanceState, cancellationToken);
        await appContext.SaveChangesAsync(cancellationToken);
        return instanceState.InstanceDiscriminator;
    }

    private async Task<RoomClockEntity> GetOrCreateRoomClockAsync(string roomHash, CancellationToken cancellationToken)
    {
        RoomClockEntity? roomClock = await appContext.RoomClocks
            .SingleOrDefaultAsync(clock => clock.RoomHash == roomHash, cancellationToken);

        if (roomClock is not null)
        {
            return roomClock;
        }

        roomClock = new RoomClockEntity
        {
            RoomHash = roomHash,
            LastLogicalTime = 0,
        };

        await appContext.RoomClocks.AddAsync(roomClock, cancellationToken);
        return roomClock;
    }

    private static long ComposeMessageId(long logicalTime, uint instanceDiscriminator)
    {
        return checked((logicalTime << InstanceDiscriminatorBits) | instanceDiscriminator);
    }

    private static long ExtractLogicalTime(long messageId)
    {
        return messageId >> InstanceDiscriminatorBits;
    }
}
