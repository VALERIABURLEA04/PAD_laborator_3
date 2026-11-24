const replicaSetName = process.env.REPLICA_SET_NAME;
const primaryHost = process.env.PRIMARY_HOST;
const secondaryHost = process.env.SECONDARY_HOST;
const arbiterHost = process.env.ARBITER_HOST;

if (![replicaSetName, primaryHost, secondaryHost, arbiterHost].every(Boolean)) {
  throw new Error("Replica set name and all three member hosts are required.");
}

function waitUntil(predicate, description, timeoutMs = 180000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      if (predicate()) return;
    } catch (_) {
      // Elections and initial synchronization can briefly reject commands.
    }
    sleep(1000);
  }
  throw new Error("Timed out waiting for " + description);
}

try {
  rs.status();
} catch (error) {
  if (error.codeName !== "NotYetInitialized") throw error;
  rs.initiate({
    _id: replicaSetName,
    members: [
      { _id: 0, host: primaryHost, priority: 2 },
      { _id: 1, host: secondaryHost, priority: 1 },
      { _id: 2, host: arbiterHost, arbiterOnly: true }
    ]
  });
}

waitUntil(() => {
  const members = rs.status().members;
  return members.some(member => member.stateStr === "PRIMARY") &&
    members.some(member => member.stateStr === "SECONDARY") &&
    members.some(member => member.stateStr === "ARBITER");
}, replicaSetName + " primary, secondary and arbiter");

print(replicaSetName + ": " + rs.status().members
  .map(member => member.name + "=" + member.stateStr).join(", "));
