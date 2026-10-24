export type ApiError = {
  code: string
  message: string
}

/**
 * Everything that may legally differ between the two ends of a tunnel. The 3.x range fields are
 * strings because a value may be either `140` or `120-160`; the API keeps them verbatim so the
 * panel never has to agree with awg on formatting.
 */
export type ClientObfuscationOverrides = {
  jc?: number | null
  jmin?: number | null
  jmax?: number | null
  i1?: string | null
  i2?: string | null
  i3?: string | null
  i4?: string | null
  i5?: string | null
  contentPaddingAddition?: string | null
  rekeyAfterTime?: string | null
  rekeyTimeout?: string | null
  rejectAfterTime?: string | null
  keepaliveTimeout?: string | null
  maxHandshakeAttempts?: string | null
  disableCookies?: boolean | null
  persistentKeepalive?: string | null
}

export type ServerObfuscationProfile = {
  s1?: number | null
  s2?: number | null
  s3?: number | null
  s4?: number | null
  h1?: string | null
  h2?: string | null
  h3?: string | null
  h4?: string | null
  headerProtectionKey?: string | null
  randomTrailers?: boolean | null
  defaultJc?: number | null
  defaultJmin?: number | null
  defaultJmax?: number | null
  defaultI1?: string | null
  defaultI2?: string | null
  defaultI3?: string | null
  defaultI4?: string | null
  defaultI5?: string | null
  defaultContentPaddingAddition?: string | null
  defaultRekeyAfterTime?: string | null
  defaultRekeyTimeout?: string | null
  defaultRejectAfterTime?: string | null
  defaultKeepaliveTimeout?: string | null
  defaultMaxHandshakeAttempts?: string | null
  defaultDisableCookies?: boolean | null
  defaultPersistentKeepalive?: string | null
}

export type Client = {
  id: string
  name: string
  address: string
  publicKey: string
  enabled: boolean
  createdAt: string
  updatedAt: string
  obfuscation?: ClientObfuscationOverrides | null
  /** The person this config was filed under; null or absent for the ungrouped list. */
  groupId?: string | null
  /** Which fleet generation this peer's config was last rendered at; 0 if one never was. */
  configGeneration: number
  fleetGeneration: number
  /**
   * A config was handed out for this peer and the identity it was issued against has since been
   * rotated, so it works on no node and has to be handed over again. The comparison is made
   * server-side, like a node's `inSync`. A peer no config was ever rendered for is not stale:
   * nothing of its is in anybody's hands.
   */
  configStale: boolean
}

/**
 * One person, holding however many devices. Purely the panel's own filing - no node is told
 * about it - and it lives on the server rather than in this browser so every device the operator
 * signs in from reads the same arrangement. The array order *is* the arrangement, for the groups
 * themselves and for the peers inside one.
 */
export type ClientGroup = {
  id: string
  name: string
  createdAt: string
  updatedAt: string
}

export type ClientStats = {
  id: string
  latestHandshakeAt?: string | null
  receivedBytes: number
  transmittedBytes: number
  online: boolean
  nodeId?: string | null
  /** When the counters were last zeroed in the panel; null means they are lifetime totals. */
  statsResetAt?: string | null
}

/**
 * One bucket of a traffic series. `at` is the instant the bucket starts. An hourly bucket sits on
 * a UTC hour; a daily one starts at midnight in the timezone the request named, so a day is the
 * reader's own day rather than UTC's.
 */
export type UsagePoint = {
  at: string
  receivedBytes: number
  transmittedBytes: number
}

/** What one peer moved over a window. Present with zeroes for a peer that moved nothing. */
export type ClientUsage = {
  id: string
  name: string
  groupId?: string | null
  enabled: boolean
  receivedBytes: number
  transmittedBytes: number
  /** Days in the window on which this peer moved anything at all. */
  activeDays: number
  lastActiveAt?: string | null
}

export type UsageWindowKey = '24h' | '7d' | '30d' | '90d'

/** How wide one point of a series is. It follows from the window, never from the caller. */
export type UsageBucket = 'hour' | 'day'

/**
 * Traffic over time. The series is dense - a quiet hour is a zero, not a gap - so a chart drawn
 * from it shows the quiet hours instead of closing over them.
 */
export type UsageSummary = {
  window: UsageWindowKey
  bucket: UsageBucket
  from: string
  to: string
  receivedBytes: number
  transmittedBytes: number
  series: UsagePoint[]
  clients: ClientUsage[]
  /** How long history is kept. Zero means the panel is not recording any. */
  retentionDays: number
  /** The oldest hour still kept, or null when nothing has been recorded yet. */
  recordingSince?: string | null
}

export type ClientUsageSeries = {
  clientId: string
  window: UsageWindowKey
  bucket: UsageBucket
  from: string
  to: string
  receivedBytes: number
  transmittedBytes: number
  activeDays: number
  series: UsagePoint[]
  retentionDays: number
  recordingSince?: string | null
}

export type Fleet = {
  generation: number
  serverPublicKey: string
  subnet: string
  listenPort: number
  clientAllowedIps: string
  clientDns?: string | null
  tunnelMtu: number
  endpointHost: string
  obfuscation?: ServerObfuscationProfile | null
  revision: number
  clientsCount: number
  nodesCount: number
  /** Peers still holding a config from an older generation. Zero unless the identity was rotated. */
  staleConfigsCount: number
  /** The revision the identity last changed at; 0 on a fleet that has never been rotated. */
  identityRevision: number
  /** When that happened, or null until the first rotation. */
  identityRotatedAt?: string | null
}

/** One peer a rotation re-keyed: the list of people who now need a new config. */
export type RotatedClient = {
  id: string
  name: string
  publicKey: string
  keyPairReissued: boolean
}

/**
 * What a rotation did. Every config in the field stopped working when this came back, so the peer
 * list is part of the answer rather than something to go and look up afterwards.
 */
export type FleetRotation = {
  generation: number
  previousGeneration: number
  serverPublicKey: string
  revision: number
  clientKeysReissued: boolean
  shareLinksRevoked: number
  /** Probe peers re-keyed too. They render their own config from the panel and need no help. */
  probesReissued: number
  clients: RotatedClient[]
  /** What a rotation does not do: a fail-static node keeps serving the old configs until it is off. */
  warning: string
}

export type Node = {
  id: string
  name: string
  hostname?: string | null
  endpointHost?: string | null
  /** Where the agent found itself to be reachable. Null until it has reported once. */
  publicIp?: string | null
  /** The one node the failover DNS record currently points at. */
  isActive: boolean
  status: 'provisioning' | 'healthy' | 'degraded' | 'down' | 'retired'
  appliedRevision: number
  fleetRevision: number
  inSync: boolean
  interfaceUp: boolean
  backend?: string | null
  agentVersion?: string | null
  bundleSchemaVersion: number
  supportsCurrentSchema: boolean
  lastSeenAt?: string | null
  lastError?: string | null
  revoked: boolean
  enrolledAt: string
  /** Whether clients can reach it, not just whether it runs. `blocked` is up but unreachable from where probes stand. */
  health: NodeHealth
  healthReason: string
  /** `probes` when probe handshakes decided the health, `agent` when only the node's own reports did. */
  healthSource: 'probes' | 'agent'
  lastReachableAt?: string | null
  failingSince?: string | null
  probesReachable: number
  probesReporting: number
  /** Lower is tried first by automatic failover. */
  failoverPriority: number
  /** False keeps automatic failover from ever sending traffic here. */
  autoFailover: boolean
  /** What this node NATs through, or null when its agent detects the default-route interface. */
  egressInterface?: string | null
  /** This node's own tunnel MTU, or null when it runs the fleet value. */
  mtu?: number | null
  /**
   * False means this node has not applied the last rotation: it still answers with the previous
   * key pair and preshared keys, so no config issued since can reach it. Deliberately distinct
   * from `inSync` - a node one peer behind is an inconvenience, a node an identity behind serves
   * nobody, and the panel refuses to send traffic there at all.
   */
  onCurrentIdentity: boolean
}

/**
 * The per-node half of a node's configuration. The egress interface and the MTU travel to the
 * node in its bundle, so changing one moves the fleet revision and that node restarts its
 * interface once; the name and the endpoint host are notes in the panel, like a client rename.
 *
 * The API models "no override" as null while a form input holds undefined, so the conversion
 * happens at this boundary - as everywhere else here.
 */
export type NodeSettings = {
  name: string
  endpointHost: string | null
  egressInterface: string | null
  mtu: number | null
}

export type NodeHealth = 'healthy' | 'blocked' | 'down' | 'silent' | 'unknown' | 'revoked'

export type FailoverAction = 'none' | 'wait' | 'hold' | 'stuck' | 'switch' | 'recommend' | 'failed'

export type FailoverStatus = {
  mode: 'manual' | 'automatic'
  provider: string
  providerConfigured: boolean
  checkIntervalSeconds: number
  graceSeconds: number
  cooldownSeconds: number
  nodeStaleSeconds: number
  probeStaleSeconds: number
  notificationChannels: string[]
  /** Null until the monitor has run its first round. */
  evaluatedAt?: string | null
  action?: FailoverAction | null
  message?: string | null
  activeNodeId?: string | null
  targetNodeId?: string | null
}

export type ProbeResult = {
  nodeId: string
  address: string
  outcome: 'reachable' | 'unreachable' | 'error'
  checkedAt: string
  lastReachableAt?: string | null
  latencyMs?: number | null
  detail?: string | null
  /**
   * Whether the handshake completed. True on an `unreachable` result is the DPI pattern: the
   * handshake gets through and the traffic after it does not.
   */
  handshake?: boolean | null
}

export type Probe = {
  id: string
  name: string
  hostname?: string | null
  agentVersion?: string | null
  lastSeenAt?: string | null
  revoked: boolean
  enrolledAt: string
  results: ProbeResult[]
}

/**
 * One bucket of a node availability series. The counters are samples - one per failover round -
 * so `samples` is the denominator of any share taken from the rest. Zero samples means the panel
 * recorded nothing in that bucket, which is not the same as a bucket the node spent down.
 */
export type NodeAvailabilityPoint = {
  at: string
  samples: number
  healthy: number
  blocked: number
  down: number
  silent: number
  unknown: number
}

export type NodeAvailability = {
  nodeId: string
  name: string
  revoked: boolean
  samples: number
  healthy: number
  blocked: number
  down: number
  silent: number
  unknown: number
  /** Share of samples the node was healthy in, 0 to 1. Null when nothing was recorded at all. */
  uptime?: number | null
  series: NodeAvailabilityPoint[]
}

/** What one probe saw of one node. `handshakes` beside `unreachable` says which block it was. */
export type ProbeNodeAvailability = {
  nodeId: string
  reachable: number
  unreachable: number
  errors: number
  handshakes: number
  /** Share of the rounds the probe actually ran that got traffic through. Null when it ran none. */
  reachability?: number | null
}

export type ProbeAvailability = {
  probeId: string
  name: string
  revoked: boolean
  nodes: ProbeNodeAvailability[]
}

/**
 * How the fleet stood over a window: per node as the panel judged it, and per probe as each
 * vantage point saw it. The second is what names the network a block is in.
 */
export type AvailabilitySummary = {
  window: UsageWindowKey
  bucket: UsageBucket
  from: string
  to: string
  nodes: NodeAvailability[]
  probes: ProbeAvailability[]
  /** How long history is kept. Zero means the panel is not recording any. */
  retentionDays: number
  /** The oldest hour still kept, or null when nothing has been recorded yet. */
  recordingSince?: string | null
}

export type DnsStatus = {
  provider: string
  providerConfigured: boolean
  recordName: string
  recordType: string
  ttl: number
  activeNodeId?: string | null
  activeNodeName?: string | null
  targetAddress?: string | null
  resolvedAddresses: string[]
  matches: boolean
  activatedAt?: string | null
  warning?: string | null
}

export type EnrollmentToken = {
  token: string
  nodeName: string
  expiresAt: string
  installCommand: string
}

export type ClientShare = {
  token: string
  clientName: string
  expiresAt: string
  url: string
}

export type AuditEvent = {
  id: number
  at: string
  kind: string
  actor?: string | null
  nodeId?: string | null
  message: string
}

export type GeneratedKey = {
  key: string
}

export type ImportResult = {
  clientsImported: number
  revision: number
  warnings: string[]
}

/** How one panel of a pair reads at a glance. A short vocabulary on purpose. */
export type PanelState = 'active' | 'replicating' | 'never_synced' | 'silent' | 'revoked'

export type Panel = {
  id: string
  name: string
  hostname?: string | null
  /** Where the other panel reaches this one. Null means the pair cannot watch it. */
  url?: string | null
  publicIp?: string | null
  version?: string | null
  role: 'active' | 'standby'
  term: number
  /** True for the panel answering the request. */
  isSelf: boolean
  isActive: boolean
  revoked: boolean
  enrolledAt: string
  /** When the configuration it holds was published, or null if it has never applied any. */
  snapshotAt?: string | null
  lastSeenAt?: string | null
  lastError?: string | null
  /** How far behind its configuration is, in seconds. Null when it holds none. */
  lagSeconds?: number | null
  state: PanelState
  stateReason: string
}

export type PanelFailoverAction = 'none' | 'wait' | 'hold' | 'recommend' | 'refused' | 'promote'

/**
 * The pair as the panel answering sees it. Everything here is about *this* panel except the
 * list: which role it is in, how its replication is going, and what the last round concluded.
 */
export type PanelPair = {
  role: 'active' | 'standby'
  panelId: string
  name: string
  /** Monotonic, one per promotion. The whole of the fencing: the higher term wins. */
  term: number
  activePanelId?: string | null
  promotedAt?: string | null
  mode: 'manual' | 'automatic'
  /** The operator's brake. A held standby never promotes itself, however armed. */
  held: boolean
  enrolled: boolean
  /** False means promotion is refused: a panel with no fleet state would publish an empty one. */
  hasFleetState: boolean
  syncedAt?: string | null
  snapshotAt?: string | null
  syncError?: string | null
  syncIntervalSeconds: number
  graceSeconds: number
  cooldownSeconds: number
  witnessConfigured: boolean
  /** Whether this panel could reach anything outside the pair, or null if it had no reason to ask. */
  witnessOnline?: boolean | null
  provider: string
  providerConfigured: boolean
  recordName?: string | null
  url?: string | null
  publicIp?: string | null
  evaluatedAt?: string | null
  action?: PanelFailoverAction | null
  message?: string | null
  panels: Panel[]
  warning?: string | null
}

export type PanelSwitch = {
  term: number
  warning?: string | null
  pair: PanelPair
}
