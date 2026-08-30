"""
Broker-side conformance check for the topology in
src/DispatchApi/Messaging/DispatchTopology.cs.

The xUnit suite covers the application's own rules — which events are published
and how the handler behaves — with no broker involved. This covers the half
those tests deliberately cannot: that RabbitMQ actually accepts these queue
arguments, that the topic binding routes what it should and refuses what it
should not, and that both routes into the dead-letter queue behave the way the
code comments claim they do.

    pip install pika
    docker compose up -d rabbitmq
    python3 tools/verify_topology.py

It creates and destroys its own queues, so it is safe to run against a local
broker and pointless to run against a shared one.
"""
import json, sys, time, uuid
import pika
from pika.exceptions import UnroutableError

EXCHANGE   = "dispatch.events"
DLX        = "dispatch.events.dlx"
QUEUE      = "dispatch.notifications"
DLQ        = "dispatch.notifications.dlq"
BINDING    = "incident.*"
DELIVERY_LIMIT = 5

results = []
def check(name, ok, detail=""):
    results.append((name, ok, detail))
    print(("  PASS  " if ok else "  FAIL  ") + name + (f"   [{detail}]" if detail else ""))

params = pika.ConnectionParameters("localhost", heartbeat=30)
conn = pika.BlockingConnection(params)
ch = conn.channel()

# --- clean slate so reruns are meaningful -----------------------------------
for q in (QUEUE, DLQ):
    try: ch.queue_delete(queue=q)
    except Exception: conn = pika.BlockingConnection(params); ch = conn.channel()
for x in (EXCHANGE, DLX):
    try: ch.exchange_delete(exchange=x)
    except Exception: conn = pika.BlockingConnection(params); ch = conn.channel()

# --- 1. topology declaration -------------------------------------------------
ch.exchange_declare(exchange=EXCHANGE, exchange_type="topic", durable=True, auto_delete=False)
ch.exchange_declare(exchange=DLX, exchange_type="fanout", durable=True, auto_delete=False)

q = ch.queue_declare(
    queue=QUEUE, durable=True, exclusive=False, auto_delete=False,
    arguments={
        "x-queue-type": "quorum",
        "x-dead-letter-exchange": DLX,
        "x-delivery-limit": DELIVERY_LIMIT,
    })
check("quorum queue accepts x-dead-letter-exchange and x-delivery-limit", True)

ch.queue_bind(queue=QUEUE, exchange=EXCHANGE, routing_key=BINDING)
ch.queue_declare(queue=DLQ, durable=True, exclusive=False, auto_delete=False,
                 arguments={"x-queue-type": "quorum"})
ch.queue_bind(queue=DLQ, exchange=DLX, routing_key="")
check("bindings declared (incident.* -> notifications, fanout -> dlq)", True)

# --- 2. publisher confirms + persistence ------------------------------------
ch.confirm_delivery()

def publish(routing_key, payload, mandatory=True):
    ch.basic_publish(
        exchange=EXCHANGE,
        routing_key=routing_key,
        body=json.dumps(payload).encode(),
        properties=pika.BasicProperties(
            content_type="application/json",
            delivery_mode=2,                      # persistent
            message_id=uuid.uuid4().hex,
            type=routing_key,
        ),
        mandatory=mandatory)

created = {"incidentId": 1, "callType": "Structure Fire", "address": "1 Main St",
           "priority": 1, "receivedAtUtc": "2026-08-30T12:00:00+00:00"}
publish("incident.created", created)
check("confirmed publish of incident.created", True)

for rk in ("incident.assigned", "incident.cleared", "incident.closed"):
    publish(rk, {"incidentId": 1})
check("every routing key the publisher uses matches the incident.* binding", True)

# --- 3. mandatory catches a routing key nothing is bound to -----------------
unroutable_caught = False
try:
    publish("unit.statuschanged", {"unitId": 1})
except UnroutableError:
    unroutable_caught = True
check("mandatory + confirms rejects an unroutable routing key", unroutable_caught,
      "publisher would throw rather than silently drop")

time.sleep(0.5)
depth = ch.queue_declare(queue=QUEUE, durable=True, passive=True).method.message_count
check("all four incident.* events routed to the queue", depth == 4, f"depth={depth}")

# --- 4. poison message goes straight to the DLQ -----------------------------
m, props, body = ch.basic_get(queue=QUEUE, auto_ack=False)
ch.basic_nack(delivery_tag=m.delivery_tag, multiple=False, requeue=False)
time.sleep(0.5)
dlq_depth = ch.queue_declare(queue=DLQ, durable=True, passive=True).method.message_count
check("nack(requeue=False) dead-letters the message", dlq_depth == 1, f"dlq={dlq_depth}")

dm, dprops, dbody = ch.basic_get(queue=DLQ, auto_ack=True)
death = (dprops.headers or {}).get("x-death", [{}])[0]
check("dead-lettered message keeps its message_id",
      dprops.message_id is not None, f"message_id={dprops.message_id}")
check("dead-lettered message records why and from where",
      death.get("reason") == "rejected" and death.get("queue") == QUEUE,
      f"reason={death.get('reason')} queue={death.get('queue')}")

# --- 5. x-delivery-limit stops an infinite requeue loop ----------------------
# Isolate it: one message, one queue, nothing else in flight, otherwise
# basic_get just hands back a different message each round and the count means
# nothing.
ch.queue_purge(queue=QUEUE)
ch.queue_purge(queue=DLQ)
publish("incident.created", dict(created, incidentId=99))
time.sleep(0.4)

deliveries = 0
while deliveries < DELIVERY_LIMIT + 5:
    m, props, body = ch.basic_get(queue=QUEUE, auto_ack=False)
    if m is None:
        break
    deliveries += 1
    ch.basic_nack(delivery_tag=m.delivery_tag, multiple=False, requeue=True)
    time.sleep(0.2)

# Measured, not assumed: x-delivery-limit counts REQUEUES, not deliveries, so
# a message with a limit of 5 is handed to a consumer 6 times before the
# broker gives up on it.
check("a repeatedly-requeued message is delivered exactly x-delivery-limit + 1 times",
      deliveries == DELIVERY_LIMIT + 1, f"deliveries={deliveries}, limit={DELIVERY_LIMIT}")

time.sleep(1.0)
q_depth = ch.queue_declare(queue=QUEUE, durable=True, passive=True).method.message_count
dlq_depth = ch.queue_declare(queue=DLQ, durable=True, passive=True).method.message_count
check("it then leaves the queue for the DLQ instead of looping forever",
      q_depth == 0 and dlq_depth == 1, f"queue={q_depth} dlq={dlq_depth}")

# --- 6. ack after processing removes it; unacked stays put ------------------
publish("incident.created", dict(created, incidentId=100))
time.sleep(0.4)
before = ch.queue_declare(queue=QUEUE, durable=True, passive=True).method.message_count
m, props, body = ch.basic_get(queue=QUEUE, auto_ack=False)
ch.basic_ack(delivery_tag=m.delivery_tag)
time.sleep(0.4)
after = ch.queue_declare(queue=QUEUE, durable=True, passive=True).method.message_count
check("ack removes the message from the queue", before == 1 and after == 0, f"{before} -> {after}")

conn.close()

failed = [r for r in results if not r[1]]
print()
print(f"{len(results) - len(failed)}/{len(results)} checks passed")
sys.exit(1 if failed else 0)
