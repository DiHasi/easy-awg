export type ApiError = {
  code: string
  message: string
}

export type ClientObfuscationOverrides = {
  jc?: number | null
  jmin?: number | null
  jmax?: number | null
  i1?: string | null
  i2?: string | null
  i3?: string | null
  i4?: string | null
  i5?: string | null
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
  defaultJc?: number | null
  defaultJmin?: number | null
  defaultJmax?: number | null
  defaultI1?: string | null
  defaultI2?: string | null
  defaultI3?: string | null
  defaultI4?: string | null
  defaultI5?: string | null
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
}

export type ClientStats = {
  id: string
  latestHandshakeAt?: string | null
  receivedBytes: number
  transmittedBytes: number
  online: boolean
  nodeId?: string | null
}

export type Fleet = {
  generation: number
  serverPublicKey: string
  subnet: string
  listenPort: number
  clientAllowedIps: string
  clientDns?: string | null
  endpointHost: string
  obfuscation?: ServerObfuscationProfile | null
  revision: number
  clientsCount: number
  nodesCount: number
}

export type Node = {
  id: string
  name: string
  hostname?: string | null
  endpointHost?: string | null
  status: 'provisioning' | 'healthy' | 'degraded' | 'down' | 'retired'
  appliedRevision: number
  fleetRevision: number
  inSync: boolean
  interfaceUp: boolean
  backend?: string | null
  agentVersion?: string | null
  lastSeenAt?: string | null
  lastError?: string | null
  revoked: boolean
  enrolledAt: string
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

export type ImportResult = {
  clientsImported: number
  revision: number
  warnings: string[]
}
