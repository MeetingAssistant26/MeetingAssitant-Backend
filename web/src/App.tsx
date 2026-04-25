import { useMemo, useState } from 'react';
import {
  AudioConference,
  LiveKitRoom,
} from '@livekit/components-react';

type DevUser = {
  number: number;
  label: string;
  role: string;
};

type AuthResponse = {
  accessToken: string;
  expiresIn: number;
  userId: string;
  email: string;
  displayName: string;
  organizationId: string;
  meetingId: string;
};

type JoinTokenResponse = {
  accessToken: string;
  roomName: string;
  serverUrl: string;
  expiresAtUtc: string;
  permissions: {
    canPublish: boolean;
    canSubscribe: boolean;
    canModerate: boolean;
    canPublishData: boolean;
  };
};

const DEV_USERS: DevUser[] = [
  { number: 1, label: 'Test User 1', role: 'Host' },
  { number: 2, label: 'Test User 2', role: 'Participant' },
];

export default function App() {
  const [auth, setAuth] = useState<AuthResponse | null>(null);
  const [token, setToken] = useState<string | null>(null);
  const [serverUrl, setServerUrl] = useState<string | null>(null);
  const [roomName, setRoomName] = useState<string | null>(null);
  const [connecting, setConnecting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const canJoin = useMemo(
    () => auth !== null && !connecting,
    [auth, connecting]
  );

  async function loginAs(userNumber: number) {
    setError(null);
    try {
      const res = await fetch('/api/dev/auth/login', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ user: userNumber }),
      });

      if (!res.ok) {
        const err = await res.json().catch(() => ({}));
        throw new Error(err.error || `Login failed (${res.status})`);
      }

      const data: AuthResponse = await res.json();
      setAuth(data);
    } catch (e: any) {
      setError(e.message);
    }
  }

  async function joinMeeting() {
    if (!auth) return;
    setConnecting(true);
    setError(null);

    try {
      const res = await fetch(
        `/api/organizations/${auth.organizationId}/meetings/${auth.meetingId}/session/join-token`,
        {
          method: 'POST',
          headers: {
            'Content-Type': 'application/json',
            Authorization: `Bearer ${auth.accessToken}`,
          },
          body: JSON.stringify({ displayName: auth.displayName }),
        }
      );

      if (!res.ok) {
        const err = await res.json().catch(() => ({}));
        throw new Error(err.title || `Join failed (${res.status})`);
      }

      const data: JoinTokenResponse = await res.json();
      setToken(data.accessToken);
      setServerUrl(data.serverUrl);
      setRoomName(data.roomName);
    } catch (e: any) {
      setError(e.message);
    } finally {
      setConnecting(false);
    }
  }

  function disconnect() {
    setToken(null);
    setServerUrl(null);
    setRoomName(null);
  }

  function logout() {
    disconnect();
    setAuth(null);
    setError(null);
  }

  // ─── LiveKit Room View ───────────────────────────────────
  if (token && serverUrl) {
    return (
      <div
        style={{
          height: '100vh',
          width: '100vw',
          display: 'flex',
          flexDirection: 'column',
          overflow: 'hidden',
        }}
      >
        <LiveKitRoom
          token={token}
          serverUrl={serverUrl}
          connect={true}
          audio={true}
          video={false}
          onDisconnected={() => {
            setToken(null);
            setServerUrl(null);
            setRoomName(null);
          }}
          style={{ height: '100%', display: 'flex', flexDirection: 'column' }}
        >
          <div style={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
            <AudioConference />
          </div>

          <div
            style={{
              position: 'fixed',
              top: 12,
              right: 12,
              zIndex: 1000,
              display: 'flex',
              gap: 8,
            }}
          >
            <div
              style={{
                padding: '8px 12px',
                background: 'rgba(0,0,0,0.6)',
                color: '#fff',
                borderRadius: 8,
                fontSize: 12,
              }}
            >
              {auth?.displayName} — {roomName}
            </div>
            <button
              onClick={disconnect}
              style={{
                padding: '10px 14px',
                fontWeight: 700,
                borderRadius: 8,
                border: '1px solid #ddd',
                background: '#fff',
                cursor: 'pointer',
              }}
            >
              Leave Room
            </button>
          </div>
        </LiveKitRoom>
      </div>
    );
  }

  // ─── Join View ──────────────────────────────────────────
  if (auth) {
    return (
      <div
        style={{
          padding: 24,
          maxWidth: 720,
          margin: '0 auto',
          fontFamily: 'system-ui',
        }}
      >
        <h1 style={{ marginBottom: 8 }}>LiveKit Test Room</h1>
        <p style={{ marginTop: 0, opacity: 0.7 }}>
          Logged in as <strong>{auth.displayName}</strong> ({auth.email})
        </p>

        <div
          style={{
            background: '#f5f5f5',
            padding: 16,
            borderRadius: 8,
            marginBottom: 20,
            fontSize: 13,
          }}
        >
          <div>
            <strong>Meeting:</strong> {auth.meetingId}
          </div>
          <div>
            <strong>Organization:</strong> {auth.organizationId}
          </div>
        </div>

        {error && (
          <div
            style={{
              background: '#ffe0e0',
              color: '#c00',
              padding: 12,
              borderRadius: 6,
              marginBottom: 16,
            }}
          >
            {error}
          </div>
        )}

        <div style={{ display: 'flex', gap: 12 }}>
          <button
            onClick={joinMeeting}
            disabled={!canJoin}
            style={{
              padding: 14,
              fontWeight: 600,
              flex: 1,
              fontSize: 16,
              borderRadius: 8,
              border: 'none',
              background: connecting ? '#ccc' : '#e94560',
              color: '#fff',
              cursor: connecting ? 'not-allowed' : 'pointer',
            }}
          >
            {connecting ? 'Connecting...' : 'Join Meeting'}
          </button>
          <button
            onClick={logout}
            style={{
              padding: 14,
              fontWeight: 600,
              fontSize: 16,
              borderRadius: 8,
              border: '1px solid #ddd',
              background: '#fff',
              cursor: 'pointer',
            }}
          >
            Switch User
          </button>
        </div>
      </div>
    );
  }

  // ─── User Selection View ────────────────────────────────
  return (
    <div
      style={{
        padding: 24,
        maxWidth: 480,
        margin: '0 auto',
        fontFamily: 'system-ui',
        textAlign: 'center',
        marginTop: '10vh',
      }}
    >
      <h1 style={{ marginBottom: 8 }}>LiveKit Prototype</h1>
      <p style={{ marginTop: 0, opacity: 0.7, marginBottom: 32 }}>
        Select a test user to join the multi-user meeting room.
      </p>

      {error && (
        <div
          style={{
            background: '#ffe0e0',
            color: '#c00',
            padding: 12,
            borderRadius: 6,
            marginBottom: 20,
          }}
        >
          {error}
        </div>
      )}

      <div style={{ display: 'grid', gap: 16 }}>
        {DEV_USERS.map((u) => (
          <button
            key={u.number}
            onClick={() => loginAs(u.number)}
            style={{
              padding: 20,
              fontSize: 18,
              fontWeight: 600,
              borderRadius: 12,
              border: '2px solid #e94560',
              background: '#fff',
              color: '#e94560',
              cursor: 'pointer',
              transition: 'all 0.2s',
            }}
            onMouseEnter={(e) => {
              e.currentTarget.style.background = '#e94560';
              e.currentTarget.style.color = '#fff';
            }}
            onMouseLeave={(e) => {
              e.currentTarget.style.background = '#fff';
              e.currentTarget.style.color = '#e94560';
            }}
          >
            <div>{u.label}</div>
            <div style={{ fontSize: 13, opacity: 0.8, marginTop: 4 }}>
              Role: {u.role}
            </div>
          </button>
        ))}
      </div>

      <p style={{ marginTop: 32, fontSize: 12, opacity: 0.5 }}>
        Development mode only. Both users share org "Dev Test Org" and
        meeting "Dev LiveKit Test Meeting".
      </p>
    </div>
  );
}
