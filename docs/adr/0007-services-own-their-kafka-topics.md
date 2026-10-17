---
status: accepted
---

# Each service creates the topics it owns; broker auto-create is off

In development each service creates the topics it produces to at startup with the Kafka AdminClient, using the partitions and retention from the data model, and the broker runs with `auto.create.topics.enable=false`. A misspelled topic name then fails loudly instead of silently creating a one-partition topic with default retention.
