-- Hot-window maintenance. ADR-005.
--
-- This runs as ONE script rather than a MULTI/EXEC transaction because the
-- sequence contains a read-then-act step: the ids to delete from the payload hash
-- are the result of the ZRANGE, and MULTI/EXEC cannot consume a reply from inside
-- the transaction it is queuing. Issued as separate round trips across five
-- replicas, another replica's writes interleave between the ZRANGE and the HDEL,
-- and payloads are left orphaned from the index - a hash that grows without bound
-- and holds records the window can no longer reach.
--
-- Redis executes a script atomically, so every command below observes the same
-- snapshot of both keys.
--
-- KEYS[1]=tx:recent  KEYS[2]=tx:payload
-- ARGV[1]=id  ARGV[2]=occurredAtMs  ARGV[3]=json  ARGV[4]=maxWindow

-- ZADD on an existing member updates its score in place, so a status change moves
-- the entry rather than adding a second one. That is why the index is a sorted set
-- and not a list: a list would need a read-modify-write to achieve the same thing.
redis.call('ZADD', KEYS[1], ARGV[2], ARGV[1])
redis.call('HSET', KEYS[2], ARGV[1], ARGV[3])

local excess = redis.call('ZCARD', KEYS[1]) - tonumber(ARGV[4])
if excess > 0 then
    -- Rank 0 is the lowest score under the default ascending order, so this is the
    -- oldest OccurredAt: eviction follows the producer's timestamp, not arrival order.
    local evicted = redis.call('ZRANGE', KEYS[1], 0, excess - 1)
    redis.call('HDEL', KEYS[2], unpack(evicted))
    redis.call('ZREMRANGEBYRANK', KEYS[1], 0, excess - 1)
end
return 1
