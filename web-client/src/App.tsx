import { useCallback, useEffect, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import { useAuth0 } from '@auth0/auth0-react'
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr'
import type { HubConnection } from '@microsoft/signalr'
import { advanceMatchCursor } from './matchSession'
import './App.css'

type RoomPlayer = { playerId: string; isOwner: boolean; isReady: boolean }
type Room = {
  roomId: string
  visibility: 'public' | 'private'
  status: string
  playerCount: number
  capacity: number
  isOwner: boolean
  isReady: boolean
  players: RoomPlayer[]
  matchId: string | null
}
type CreatedRoom = { room: Room; inviteCode: string | null }
type MatchSnapshot = {
  matchId: string
  sequence: number
  roundNumber: number
  status: 'active' | 'completed' | 'waitingForNicknames'
  winnerId: string | null
  hasSubmittedPlan: boolean
  roundDeadlineAt: string | null
  terminalReason: string | null
  you: { playerId: string; isHost: boolean; nickname: string | null; health: number; energy: number; usedBlockades: number; hasUsedSpecialAttack: boolean }
  opponent: { health: number; isHost: boolean; nickname: string | null }
}
type PlanAction = 'Attack' | 'Block' | 'Reserve' | 'SpecialAttack'
type RoundResult = { roundNumber: number; yourDamage: number; opponentDamage: number; yourCriticalHits: number; opponentCriticalHits: number; yourAttacksBlocked: number; opponentAttacksBlocked: number }
type MatchEndReason = 'inactivity' | 'abandonment' | null
type HistoryEntry = { matchId: string; status: string; roundNumber: number; result: 'active' | 'won' | 'lost' | 'draw'; opponentNickname: string | null; createdAt: string; completedAt: string | null; terminalReason: string | null }
type MatchHistory = { items: HistoryEntry[]; page: number; pageSize: number; total: number }
type AdminMatch = { matchId: string; roundNumber: number; sequence: number; lastActivityAt: string; roundDeadlineAt: string | null; participantSubjects: string[] }
type AdminConnection = { connectionId: string; subject: string; connectedAt: string }
const lobbyBanners = ['/assets/banner/banner.jpg', '/assets/banner/bannerS2.jpg', '/assets/banner/bannerS3.jpg']

// Renderiza el inicio de sesión, el estado de conexión y las opciones de sala y emparejamiento.
function App() {
  const { error, isAuthenticated, isLoading, user, getAccessTokenSilently, loginWithRedirect, logout } = useAuth0()
  const [room, setRoom] = useState<Room | null>(null)
  const [publicRooms, setPublicRooms] = useState<Room[]>([])
  const [visibility, setVisibility] = useState<'public' | 'private'>('public')
  const [joinRoomId, setJoinRoomId] = useState('')
  const [inviteCode, setInviteCode] = useState('')
  const [createdInviteCode, setCreatedInviteCode] = useState<string | null>(null)
  const [roomMessage, setRoomMessage] = useState<string | null>(null)
  const [roomError, setRoomError] = useState<string | null>(null)
  const [connectionStatus, setHubStatus] = useState<'disconnected' | 'connecting' | 'connected' | 'error'>('disconnected')
  const [connectionError, setHubError] = useState<string | null>(null)
  const hubStatus = isAuthenticated ? connectionStatus : 'disconnected'
  const hubError = isAuthenticated ? connectionError : null
  const [hubConnection, setHubConnection] = useState<HubConnection | null>(null)
  const [queueRequestId, setQueueRequestId] = useState<string | null>(null)
  const [queueStatus, setQueueStatus] = useState<'idle' | 'queued' | 'cancelled' | 'timedOut' | 'matched'>('idle')
  const [quickMatchId, setQuickMatchId] = useState<string | null>(null)
  const [recoveredMatchId, setRecoveredMatchId] = useState<string | null>(() => localStorage.getItem('game.activeMatchId'))
  const [matchSnapshot, setMatchSnapshot] = useState<MatchSnapshot | null>(null)
  const [planCounts, setPlanCounts] = useState<Record<PlanAction, number>>({ Attack: 0, Block: 0, Reserve: 0, SpecialAttack: 0 })
  const [planPending, setPlanPending] = useState(false)
  const [roundResult, setRoundResult] = useState<RoundResult | null>(null)
  const [matchError, setMatchError] = useState<string | null>(null)
  const [matchEndReason, setMatchEndReason] = useState<MatchEndReason>(null)
  const [nickname, setNickname] = useState('')
  const [history, setHistory] = useState<MatchHistory | null>(null)
  const [historyOpen, setHistoryOpen] = useState(false)
  const [historyError, setHistoryError] = useState<string | null>(null)
  const [historyLoading, setHistoryLoading] = useState(false)
  const [lobbyBannerIndex, setLobbyBannerIndex] = useState(0)
  const [isAdmin, setIsAdmin] = useState(false)
  const [showAdmin, setShowAdmin] = useState(false)
  const [adminMatches, setAdminMatches] = useState<AdminMatch[]>([])
  const [adminConnections, setAdminConnections] = useState<AdminConnection[]>([])
  const [adminError, setAdminError] = useState<string | null>(null)
  const [adminBusy, setAdminBusy] = useState(false)
  const [suspendSubject, setSuspendSubject] = useState('')
  const [suspendReason, setSuspendReason] = useState('')
  const latestMatchSequence = useRef(-1)
  const sequencedMatchId = useRef<string | null>(null)
  const expectedMatchId = useRef<string | null>(recoveredMatchId)
  const currentRoomId = useRef<string | null>(null)

  // Alterna las imágenes del banner cada tres segundos mientras la aplicación está abierta.
  useEffect(() => {
    const timer = window.setInterval(() => setLobbyBannerIndex((index) => (index + 1) % lobbyBanners.length), 3000)
    return () => window.clearInterval(timer)
  }, [])

  const roomMatchId = room?.status === 'Partida finalizada' ? null : room?.matchId
  const activeMatchId = quickMatchId ?? roomMatchId ?? recoveredMatchId

  const serverUrl = import.meta.env.VITE_GAME_SERVER_URL?.replace(/\/+$/, '') ?? window.location.origin

  const selectActiveMatch = useCallback((matchId: string) => {
    const changedMatch = expectedMatchId.current !== matchId
    expectedMatchId.current = matchId
    if (changedMatch) {
      sequencedMatchId.current = matchId
      latestMatchSequence.current = -1
    }
    localStorage.setItem('game.activeMatchId', matchId)
    setRecoveredMatchId(matchId)
    setMatchSnapshot((current) => current?.matchId === matchId ? current : null)
    setRoundResult(null)
    setMatchEndReason(null)
    setPlanPending(false)
    setPlanCounts({ Attack: 0, Block: 0, Reserve: 0, SpecialAttack: 0 })
  }, [])

  const finishActiveMatch = useCallback((matchId: string) => {
    if (localStorage.getItem('game.activeMatchId') === matchId)
      localStorage.removeItem('game.activeMatchId')
    setRecoveredMatchId((current) => current === matchId ? null : current)
    setQuickMatchId((current) => current === matchId ? null : current)
    setRoom((current) => current?.matchId === matchId
      ? { ...current, status: 'Partida finalizada' }
      : current)
  }, [])

  const acceptMatchMessage = useCallback((message: { matchId: string; sequence: number }, allowEqualSequence = false) => {
    const decision = advanceMatchCursor(
      { matchId: sequencedMatchId.current, sequence: latestMatchSequence.current },
      expectedMatchId.current,
      message,
      allowEqualSequence,
    )
    if (!decision.accepted) return false
    sequencedMatchId.current = decision.cursor.matchId
    latestMatchSequence.current = decision.cursor.sequence
    expectedMatchId.current = message.matchId
    return true
  }, [])

  // Abre y mantiene la conexión autenticada de SignalR mientras el usuario inició sesión.
  useEffect(() => {
    if (!isAuthenticated) {
      return
    }

    let disposed = false
    let connection: HubConnection | null = null

    // Solicita el token de Auth0 y establece la conexión con el Hub del servidor.
    const startConnection = async () => {
      if (!serverUrl) {
        setHubStatus('error')
        setHubError('Falta VITE_GAME_SERVER_URL en web-client/.env.local.')
        return
      }

      setHubStatus('connecting')
      setHubError(null)
      // Obtiene el token que SignalR enviará al autenticar la conexión.
      const accessTokenFactory = async () => {
        const token = await getAccessTokenSilently()
        if (!token) throw new Error('Auth0 no devolvió un token de acceso para la API.')
        return token
      }

      connection = new HubConnectionBuilder()
        .withUrl(`${serverUrl}/hubs/game`, { accessTokenFactory })
        .withAutomaticReconnect()
        .configureLogging(LogLevel.Warning)
        .build()

      connection.onreconnecting(() => { if (!disposed) setHubStatus('connecting') })
      connection.onreconnected(() => { if (!disposed) setHubStatus('connected') })
      connection.on('matchmaking.status', (message: { status: string }) => {
        if (disposed) return
        setQueueStatus(message.status as 'queued' | 'cancelled' | 'timedOut')
      })
      connection.on('match.found', (message: { matchId: string }) => {
        if (disposed) return
        selectActiveMatch(message.matchId)
        setQuickMatchId(message.matchId)
        setQueueStatus('matched')
        setQueueRequestId(null)
      })
      connection.on('room.match.started', (message: { roomId: string; matchId: string }) => {
        if (disposed || currentRoomId.current !== message.roomId) return
        selectActiveMatch(message.matchId)
        setRoom((current) => current?.roomId === message.roomId
          ? { ...current, matchId: message.matchId, status: 'Partida en curso' }
          : current)
      })
      connection.on('match.state.snapshot', (message: MatchSnapshot) => {
        if (!disposed) {
          if (!acceptMatchMessage(message, true)) return
          if (message.status === 'completed') finishActiveMatch(message.matchId)
          else {
            localStorage.setItem('game.activeMatchId', message.matchId)
            setRecoveredMatchId(message.matchId)
          }
          setMatchSnapshot(message)
          setMatchError(null)
          setPlanCounts({ Attack: 0, Block: 0, Reserve: 0, SpecialAttack: 0 })
          setPlanPending(message.hasSubmittedPlan)
        }
      })
      connection.on('round.plan.accepted', (message: { matchId: string; sequence: number }) => {
        if (!disposed && message.matchId === expectedMatchId.current && message.sequence >= latestMatchSequence.current) setPlanPending(true)
      })
      connection.on('round.resolved', (message: MatchSnapshot & RoundResult) => {
        if (disposed) return
        if (!acceptMatchMessage(message)) return
        setMatchSnapshot({ ...message, roundNumber: message.status === 'active' ? message.roundNumber + 1 : message.roundNumber })
        setRoundResult(message)
        setPlanCounts({ Attack: 0, Block: 0, Reserve: 0, SpecialAttack: 0 })
        setPlanPending(false)
        setMatchError(null)
        setMatchEndReason(null)
        if (message.status === 'completed') finishActiveMatch(message.matchId)
      })
      connection.on('match.ended', (message: MatchSnapshot & { reason: string }) => {
        if (disposed) return
        if (!acceptMatchMessage(message)) return
        setMatchSnapshot(message)
        setMatchEndReason(message.reason === 'abandonment' || message.reason === 'inactivity' ? message.reason : null)
        setRoundResult(null)
        setPlanPending(false)
        finishActiveMatch(message.matchId)
      })
      connection.onclose((closeError) => {
        if (disposed) return
        setHubConnection(null)
        setHubStatus(closeError ? 'error' : 'disconnected')
        setHubError(closeError?.message ?? null)
      })

      try {
        await connection.start()
        if (!disposed) {
          setHubConnection(connection)
          setHubStatus('connected')
        }
      } catch (connectError) {
        if (!disposed) {
          setHubStatus('error')
          setHubError(connectError instanceof Error ? connectError.message : 'No se pudo conectar.')
        }
      }
    }

    void startConnection()
    return () => {
      disposed = true
      if (connection) void connection.stop()
    }
  }, [acceptMatchMessage, finishActiveMatch, getAccessTokenSilently, isAuthenticated, selectActiveMatch, serverUrl])

  // Suscribe la conexión al aviso privado de inicio de la sala actual.
  useEffect(() => {
    if (!room?.roomId || !hubConnection || hubStatus !== 'connected') return
    void hubConnection.invoke('JoinRoom', { type: 'room.join', version: 1, roomId: room.roomId }).catch((joinError: unknown) => {
      setRoomError(joinError instanceof Error ? joinError.message : 'No se pudo conectar a la sala.')
    })
  }, [room?.roomId, hubConnection, hubStatus])

  // Entra al canal privado cuando el servidor crea la partida desde una sala o la cola rápida.
  useEffect(() => {
    if (!activeMatchId || !hubConnection || hubStatus !== 'connected') return
    if (expectedMatchId.current !== activeMatchId) selectActiveMatch(activeMatchId)
    void hubConnection.invoke('JoinMatch', { type: 'match.join', version: 1, matchId: activeMatchId }).catch((joinError: unknown) => {
      setMatchError(joinError instanceof Error ? joinError.message : 'No se pudo cargar la partida.')
    })
  }, [activeMatchId, hubConnection, hubStatus, selectActiveMatch])

  // Descubre la partida más reciente desde PostgreSQL cuando una pestaña nueva no conoce su identificador.
  useEffect(() => {
    if (activeMatchId || !hubConnection || hubStatus !== 'connected') return
    // Una referencia local terminada no debe impedir que el servidor priorice otra partida activa.
    expectedMatchId.current = null
    void hubConnection.invoke('ResumeMatch', {
      type: 'match.resume', version: 1, matchId: null,
      lastSequence: latestMatchSequence.current < 0 ? null : latestMatchSequence.current,
    }).catch((resumeError: unknown) => {
      const message = resumeError instanceof Error ? resumeError.message : ''
      if (!message.includes('active_match_not_found')) setMatchError(message || 'No se pudo recuperar la partida.')
    })
  }, [activeMatchId, hubConnection, hubStatus])

  // La cola no se conserva en el servidor: una conexión que sigue esperando se reinscribe tras reconectar.
  useEffect(() => {
    if (!hubConnection || hubStatus !== 'connected' || queueStatus !== 'queued' || !queueRequestId) return
    void hubConnection.invoke('JoinMatchmakingQueue', {
      type: 'matchmaking.queue.join', version: 1, requestId: queueRequestId,
    }).catch((queueError: unknown) => {
      setQueueStatus('idle')
      setQueueRequestId(null)
      setHubError(queueError instanceof Error ? queueError.message : 'La cola se reinició; vuelve a inscribirte.')
    })
  }, [hubConnection, hubStatus, queueRequestId, queueStatus])

  // Envía una solicitud REST autenticada y convierte su respuesta JSON al tipo solicitado.
  const apiRequest = async <T,>(path: string, method = 'GET', body?: unknown): Promise<T> => {
    if (!serverUrl) throw new Error('Falta VITE_GAME_SERVER_URL en web-client/.env.local.')
    const token = await getAccessTokenSilently()
    if (!token) throw new Error('Auth0 no devolvió un token de acceso para la API.')
    const response = await fetch(`${serverUrl}${path}`, {
      method,
      headers: {
        Authorization: `Bearer ${token}`,
        ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
      },
      ...(body === undefined ? {} : { body: JSON.stringify(body) }),
    })
    if (!response.ok) {
      const problem = await response.json().catch(() => null) as { code?: string; title?: string } | null
      throw new Error(problem?.code ?? problem?.title ?? `La solicitud falló (${response.status}).`)
    }
    return response.json() as Promise<T>
  }

  // Confirma el permiso administrativo consultando un endpoint protegido por el servidor.
  useEffect(() => {
    if (!isAuthenticated || !serverUrl) { setIsAdmin(false); return }
    let cancelled = false
    void (async () => {
      try {
        const token = await getAccessTokenSilently()
        const response = await fetch(`${serverUrl}/api/admin/connections`, { headers: { Authorization: `Bearer ${token}` } })
        if (!cancelled) setIsAdmin(response.ok)
      } catch { if (!cancelled) setIsAdmin(false) }
    })()
    return () => { cancelled = true }
  }, [getAccessTokenSilently, hubStatus, isAuthenticated, serverUrl])

  // Carga las partidas activas y las conexiones visibles para el administrador.
  const refreshAdminData = async () => {
    setAdminBusy(true); setAdminError(null)
    try {
      const [matches, connections] = await Promise.all([
        apiRequest<AdminMatch[]>('/api/admin/matches/active'),
        apiRequest<AdminConnection[]>('/api/admin/connections'),
      ])
      setAdminMatches(matches); setAdminConnections(connections)
    } catch (loadError) { setAdminError(loadError instanceof Error ? loadError.message : 'No se pudieron cargar los datos administrativos.') }
    finally { setAdminBusy(false) }
  }

  // Termina una partida activa y actualiza el panel administrativo.
  const terminateAdminMatch = async (matchId: string) => {
    const reason = window.prompt('Motivo para terminar la partida:')?.trim()
    if (!reason) return
    setAdminBusy(true); setAdminError(null)
    try { await apiRequest(`/api/admin/matches/${matchId}/terminate`, 'POST', { reason }); await refreshAdminData() }
    catch (actionError) { setAdminError(actionError instanceof Error ? actionError.message : 'No se pudo terminar la partida.') }
    finally { setAdminBusy(false) }
  }

  // Suspende al usuario indicado y limpia los campos del formulario.
  const suspendAdminUser = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault(); setAdminError(null)
    try {
      await apiRequest(`/api/admin/users/${encodeURIComponent(suspendSubject)}/suspension`, 'PUT', { reason: suspendReason, expiresAt: null })
      setSuspendSubject(''); setSuspendReason('')
    } catch (actionError) { setAdminError(actionError instanceof Error ? actionError.message : 'No se pudo suspender al usuario.') }
  }

  // Ejecuta una operación de sala y muestra cualquier error en la interfaz.
  const runRoomAction = async (action: () => Promise<void>) => {
    setRoomError(null)
    setRoomMessage(null)
    try { await action() } catch (actionError) {
      setRoomError(actionError instanceof Error ? actionError.message : 'La operación de sala falló.')
    }
  }

  // Carga las salas públicas disponibles para unirse.
  const loadPublicRooms = () => runRoomAction(async () => {
    setPublicRooms(await apiRequest<Room[]>('/api/rooms'))
  })

  // Carga la página actual del historial del usuario autenticado.
  const loadHistory = async (page = 1) => {
    setHistoryLoading(true)
    setHistoryError(null)
    try { setHistory(await apiRequest<MatchHistory>(`/api/history?page=${page}&pageSize=4`)) }
    catch (historyLoadError) { setHistoryError(historyLoadError instanceof Error ? historyLoadError.message : 'No se pudo cargar el historial.') }
    finally { setHistoryLoading(false) }
  }

  // Crea una sala con la visibilidad seleccionada.
  const createRoom = () => runRoomAction(async () => {
    const created = await apiRequest<CreatedRoom>('/api/rooms', 'POST', { visibility })
    currentRoomId.current = created.room.roomId
    setRoom(created.room)
    setCreatedInviteCode(created.inviteCode)
    setRoomMessage('Sala creada.')
    await loadPublicRooms()
  })

  // Une al usuario a una sala pública o privada.
  const joinRoom = (roomId: string, code?: string) => runRoomAction(async () => {
    const joined = await apiRequest<Room>(`/api/rooms/${roomId}/join`, 'POST', { inviteCode: code ?? null })
    currentRoomId.current = joined.roomId
    setRoom(joined)
    setCreatedInviteCode(null)
    setRoomMessage('Te uniste a la sala.')
    await loadPublicRooms()
  })

  // Cambia el estado de preparación del usuario en su sala actual.
  const setReady = (isReady: boolean) => room && runRoomAction(async () => {
    const updated = await apiRequest<Room>(`/api/rooms/${room.roomId}/ready`, 'PUT', { isReady })
    if (currentRoomId.current !== updated.roomId) return
    if (updated.matchId) selectActiveMatch(updated.matchId)
    setRoom(updated)
  })

  // Actualiza los datos de la sala actual desde el servidor.
  const refreshRoom = () => room && runRoomAction(async () => {
    setRoom(await apiRequest<Room>(`/api/rooms/${room.roomId}`))
  })

  // Retira al usuario de su sala y refresca la lista pública.
  const leaveRoom = () => room && runRoomAction(async () => {
    await apiRequest(`/api/rooms/${room.roomId}/membership`, 'DELETE')
    await hubConnection?.invoke('LeaveRoom', { type: 'room.leave', version: 1, roomId: room.roomId })
    currentRoomId.current = null
    setRoom(null)
    setCreatedInviteCode(null)
    setRoomMessage('Saliste de la sala.')
    await loadPublicRooms()
  })

  // Solicita entrar en la cola rápida y asigna un identificador a esta solicitud.
  const joinQuickMatch = async () => {
    if (!hubConnection) return
    const requestId = crypto.randomUUID()
    setQueueRequestId(requestId)
    setQuickMatchId(null)
        setMatchSnapshot(null)
    setQueueStatus('queued')
    await hubConnection.invoke('JoinMatchmakingQueue', {
      type: 'matchmaking.queue.join', version: 1, requestId,
    })
  }

  // Cancela la solicitud de emparejamiento rápido actualmente activa.
  const cancelQuickMatch = async () => {
    if (!hubConnection || !queueRequestId) return
    await hubConnection.invoke('CancelMatchmakingQueue', {
      type: 'matchmaking.queue.cancel', version: 1, requestId: queueRequestId,
    })
    setQueueRequestId(null)
  }

  // Añade o quita una acción del plan sin superar la energía disponible.
  const changePlanAction = (action: PlanAction, delta: number) => {
    setPlanCounts((current) => {
      const nextCount = current[action] + delta
      if (nextCount < 0) return current
      const cost = action === 'SpecialAttack' ? 3 : 1
      const used = Object.entries(current).reduce((sum, [name, count]) => sum + count * (name === 'SpecialAttack' ? 3 : 1), 0)
      if (delta > 0 && used + cost > (matchSnapshot?.you.energy ?? 0)) return current
      return { ...current, [action]: nextCount }
    })
  }

  // Envía al servidor todas las acciones que consumen la energía de la ronda.
  const submitRoundPlan = async () => {
    if (!hubConnection || !matchSnapshot || !activeMatchId || planPending) return
    const actions = (Object.entries(planCounts) as [PlanAction, number][]).flatMap(([action, count]) => Array(count).fill(action))
    const requestId = crypto.randomUUID()
    setMatchError(null)
    try {
      await hubConnection.invoke('SubmitRoundPlan', {
        type: 'round.plan.submit', version: 1, requestId, matchId: activeMatchId,
        expectedSequence: matchSnapshot.sequence, actions,
      })
      // Si ambos planes llegaron durante la invocación, la ronda ya pudo resolverse.
      if (latestMatchSequence.current <= matchSnapshot.sequence) setPlanPending(true)
    } catch (submitError) {
      setMatchError(submitError instanceof Error ? submitError.message : 'No se pudo enviar el plan.')
    }
  }

  // Solicita abandonar la partida y confirma antes que el servidor otorgue la victoria al rival.
  const abandonMatch = async () => {
    if (!hubConnection || !activeMatchId || !window.confirm('¿Quieres abandonar la partida? Tu rival ganará automáticamente.')) return
    setMatchError(null)
    try {
      await hubConnection.invoke('AbandonMatch', activeMatchId)
    } catch (abandonError) {
      setMatchError(abandonError instanceof Error ? abandonError.message : 'No se pudo abandonar la partida.')
    }
  }

  // Envía el nickname temporal elegido para esta partida.
  const submitNickname = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    if (!hubConnection || !matchSnapshot || !activeMatchId) return
    if (!/^[\p{L}\p{N}_-]{1,10}$/u.test(nickname)) {
      setMatchError('Usa de 1 a 10 letras, números, guion bajo o guion medio.')
      return 
    }
    setMatchError(null)
    try {
      await hubConnection.invoke('SetMatchNickname', {
        type: 'match.nickname.set', version: 1, requestId: crypto.randomUUID(),
        matchId: activeMatchId, nickname,
      })
    } catch (nicknameError) {
      setMatchError(nicknameError instanceof Error ? nicknameError.message : 'No se pudo guardar el nickname.')
    }
  }

  // Vuelve al lobby después de una partida terminada o para abandonar su vista actual.
  const returnToLobby = () => {
    localStorage.removeItem('game.activeMatchId')
    expectedMatchId.current = null
    sequencedMatchId.current = null
    latestMatchSequence.current = -1
    currentRoomId.current = null
    setQuickMatchId(null)
    setRecoveredMatchId(null)
    setRoom(null)
    setQueueRequestId(null)
    setQueueStatus('idle')
    setMatchSnapshot(null)
    setRoundResult(null)
    setPlanPending(false)
    setPlanCounts({ Attack: 0, Block: 0, Reserve: 0, SpecialAttack: 0 })
  }

  if (isLoading) return <main className="auth-page"><section className="auth-card"><p>Comprobando sesión…</p></section></main>
  if (error) return <main className="auth-page"><section className="auth-card"><p className="error-message" role="alert">{error.message}</p></section></main>

  if (isAuthenticated && matchSnapshot) {
    const usedEnergy = Object.entries(planCounts).reduce((sum, [action, count]) => sum + count * (action === 'SpecialAttack' ? 3 : 1), 0)
    const remainingEnergy = matchSnapshot.you.energy - usedEnergy
    const actions: { id: PlanAction; label: string; detail: string; image: string; disabled?: boolean }[] = [
      { id: 'Attack', label: 'Ataque', detail: '5 de daño · crítico posible', image: '/assets/actions/attack.png' },
      { id: 'Block', label: 'Bloqueo', detail: `Cancela un ataque · ${Math.max(0, 5 - matchSnapshot.you.usedBlockades)} restantes`, image: '/assets/actions/block.png', disabled: matchSnapshot.you.usedBlockades + planCounts.Block >= 5 },
      { id: 'Reserve', label: 'Reservar energía', detail: 'Guarda energía para la siguiente ronda', image: '/assets/actions/saveEnergy.jpg' },
      { id: 'SpecialAttack', label: 'Ataque especial', detail: '20 de daño · x3 de energía · un solo uso', image: '/assets/actions/special.jpg', disabled: matchSnapshot.roundNumber < 5 || matchSnapshot.you.hasUsedSpecialAttack || planCounts.SpecialAttack >= 1 },
    ]
    // Elegir la imagen del jugador y del oponente según si es anfitrión o invitado.
    const playerImage = matchSnapshot.you.isHost
      ? '/assets/characters/host.png'
      : '/assets/characters/guest.png'
    const opponentImage = matchSnapshot.opponent.isHost
      ? '/assets/characters/host-enemyView.png'
      : '/assets/characters/guest-enemyView.png'

    // Determina el resultado de la partida y elige el título y la imagen correspondiente.
    const isDraw = matchSnapshot.winnerId === null
    const didWin = matchSnapshot.winnerId === matchSnapshot.you.playerId
    const winnerIsHost = didWin ? matchSnapshot.you.isHost : matchSnapshot.opponent.isHost
    const expiredWithoutPlans = isDraw && matchEndReason === 'inactivity'
    const resultTitle = isDraw
      ? matchEndReason === 'inactivity' ? 'Partida expirada' : 'Empate'
      : didWin ? 'Victoria' : 'Derrota'
    const resultImage = expiredWithoutPlans
      ? '/assets/characters/expiredMatch.jpg'
      : isDraw
        ? '/assets/characters/drawnGame.jpg'
        : winnerIsHost
          ? didWin ? '/assets/characters/hostVictory.png' : '/assets/characters/guestDefeat.png'
          : didWin ? '/assets/characters/guestVictory.png' : '/assets/characters/hostDefeat.png'
    const resultImageAlt = expiredWithoutPlans
      ? 'Ilustración de partida expirada'
      : isDraw
        ? 'Ilustración de empate'
        : didWin ? 'Ilustración de victoria' : 'Ilustración de derrota'
    const resultDescription = matchEndReason === 'abandonment'
      ? didWin ? 'Ganaste porque tu rival abandonó la partida' : 'Perdiste porque abandonaste la partida'
      : matchEndReason === 'inactivity' ? 'La partida finalizó por inactividad.' : null
    if (matchSnapshot.status === 'completed') {
      return <main className="auth-page match-page match-completed-page">
        <section className="auth-card match-result-card" role="dialog" aria-modal="true" aria-labelledby="match-result-title">
          <p className="eyebrow">PARTIDA FINALIZADA</p>
          <h1 id="match-result-title">{resultTitle}</h1>
          {resultDescription && <p className="match-result-description">{resultDescription}</p>}
          <img className="match-result-image" src={resultImage} alt={resultImageAlt} />
          {roundResult && <div className="round-result"><strong>Resultado de la ronda {roundResult.roundNumber}</strong><span>Infligiste {roundResult.yourDamage} ··· recibiste {roundResult.opponentDamage}</span><span>Críticos: {roundResult.yourCriticalHits} ··· ataques tuyos bloqueados: {roundResult.yourAttacksBlocked}</span></div>}
          <div className="lobby-return-wrap"><button className="text-button lobby-return" onClick={returnToLobby} aria-label="Volver al lobby" title="Volver al lobby"><span className="lobby-return-icon" aria-hidden="true">🏰</span></button></div>
        </section>
      </main>
    }
    if (matchSnapshot.status === 'waitingForNicknames') {
      return <main className="auth-page match-page"><section className="auth-card lobby-card match-card nickname-card">
        <p className="eyebrow">NUEVA PARTIDA</p><h1>Elige tu nickname</h1>
        <p className="supporting-text">Este nombre se usará solo en esta partida </p>
        {matchSnapshot.you.nickname ? <div className="nickname-waiting"><strong>{matchSnapshot.you.nickname}</strong><span>Tu nickname está listo. Esperando al rival…</span></div> : <form className="nickname-form" onSubmit={submitNickname}>
          <input id="match-nickname" value={nickname} onChange={(event) => setNickname(event.target.value)} maxLength={10} autoComplete="off" placeholder="Ej. Caballero_1" />
          <small>1–10 caracteres: letras, números, _ o -</small>
          {matchError && <p className="error-message" role="alert">{matchError}</p>}
          <button className="primary-button" type="submit" disabled={!hubConnection || !/^[\p{L}\p{N}_-]{1,10}$/u.test(nickname)}>Confirmar nickname</button>
        </form>}
        {matchSnapshot.opponent.nickname && <p className="nickname-rival">El rival eligió: <strong>{matchSnapshot.opponent.nickname}</strong></p>}
      </section></main>
    }
    return <main className="auth-page match-page"><section className="auth-card lobby-card match-card">
      <div className="section-heading"><div><p className="eyebrow">Batalla</p><h1>Ronda {matchSnapshot.roundNumber}</h1></div><div className="match-header-status"><span className="match-connection"><span className={`status-dot status-${hubStatus}`} />{hubStatus === 'connected' ? 'Conectado' : hubStatus === 'connecting' ? 'Conectando…' : hubStatus === 'error' ? 'Error de conexión' : 'Desconectado'}</span><span className="match-sequence">Estado #{matchSnapshot.sequence}</span></div></div>
      <section className="battlefield" aria-label="Terreno de combate">
        <div className="fighter-card"><div className="fighter-placeholder fighter-image"><img src={playerImage} alt="" /></div><p className="fighter-name">{matchSnapshot.you.nickname ?? 'Tú'}</p><div className="fighter-health"><strong>{matchSnapshot.you.health}</strong><span>/ 50 salud</span></div><div className="health-track"><span style={{ width: `${matchSnapshot.you.health * 2}%` }} /></div><span className="fighter-energy">✦ {matchSnapshot.you.energy} energía</span></div>
        <div className="battle-terrain" aria-hidden="true"><span className="terrain-line" /><span className="versus-mark">VS</span><span className="terrain-line" /></div>
        <div className="fighter-card opponent-fighter"><div className="fighter-placeholder fighter-image"><img src={opponentImage} alt="" /></div><p className="fighter-name">{matchSnapshot.opponent.nickname ?? 'Rival'}</p><div className="fighter-health"><strong>{matchSnapshot.opponent.health}</strong><span>/ 50 salud</span></div><div className="health-track opponent-health"><span style={{ width: `${matchSnapshot.opponent.health * 2}%` }} /></div><span className="fighter-energy">Contrincante</span></div>
      </section>
      <section className="room-panel plan-panel"><div className="section-heading"><div><h2>Plan de ronda</h2><p className="supporting-text">Asigna toda tu energía. El rival no verá tus acciones.</p></div><strong className="energy-badge">{remainingEnergy} / {matchSnapshot.you.energy} energía</strong></div>
        <div className="actions-bar">{actions.map((action) => <div className="action-row" key={action.id}><div className="action-art" tabIndex={0} aria-label={`${action.label}: ${action.detail}`} title={`${action.label}: ${action.detail}`}><img src={action.image} alt="" /><div className="action-tooltip"><strong>{action.label}</strong><span>{action.detail}</span></div></div><div className="action-counter"><button aria-label={`Quitar ${action.label}`} onClick={() => changePlanAction(action.id, -1)} disabled={planPending || planCounts[action.id] === 0}>−</button><strong>{planCounts[action.id]}</strong><button aria-label={`Añadir ${action.label}`} onClick={() => changePlanAction(action.id, 1)} disabled={planPending || action.disabled || remainingEnergy < (action.id === 'SpecialAttack' ? 3 : 1)}>+</button></div></div>)}</div>
        {roundResult && <div className="round-result"><strong>Resultado de la ronda {roundResult.roundNumber}</strong><span>Infligiste {roundResult.yourDamage} ··· recibiste {roundResult.opponentDamage}</span><span>Críticos: {roundResult.yourCriticalHits} ··· ataques tuyos bloqueados: {roundResult.yourAttacksBlocked}</span></div>}
        <button className="primary-button submit-plan-button" onClick={submitRoundPlan} disabled={remainingEnergy !== 0 || planPending || !hubConnection}>Enviar plan</button>
        {planPending && <p className="supporting-text plan-pending-message" role="status">Plan enviado. Esperando al rival…</p>}
        {matchError && <p className="error-message" role="alert">{matchError}</p>}
        <button className="abandon-match-button" onClick={abandonMatch} disabled={!hubConnection}>Abandonar</button>
      </section>
    </section></main>
  }

  if (showAdmin && isAdmin) return <main className="auth-page admin-page"><section className="auth-card lobby-card admin-card">
    <div className="section-heading"><h1>Administración</h1><button className="text-button" onClick={() => setShowAdmin(false)}>Volver al lobby</button></div>
    <div className="section-heading admin-section-heading"><h2>Partidas activas</h2><button className="text-button" onClick={() => void refreshAdminData()} disabled={adminBusy}>Actualizar</button></div>
    {adminMatches.length === 0 ? <p className="supporting-text">No hay partidas activas.</p> : <div className="admin-list">{adminMatches.map((match) => <article className="admin-entry" key={match.matchId}><div><strong>Ronda {match.roundNumber}</strong><span>{match.matchId}</span><span>Actividad: {new Date(match.lastActivityAt).toLocaleString()}</span></div><button className="abandon-match-button" disabled={adminBusy} onClick={() => void terminateAdminMatch(match.matchId)}>Terminar partida</button></article>)}</div>}
    <div className="section-heading admin-section-heading"><h2>Conexiones activas</h2><span>{adminConnections.length}</span></div>
    {adminConnections.length === 0 ? <p className="supporting-text">No hay conexiones activas.</p> : <div className="admin-list">{adminConnections.map((connection) => <article className="admin-entry" key={connection.connectionId}><div><strong>{connection.subject}</strong><span>Conectado: {new Date(connection.connectedAt).toLocaleString()}</span></div></article>)}</div>}
    <section className="room-panel admin-suspend-panel"><h2>Suspender usuario</h2><form onSubmit={suspendAdminUser}><input value={suspendSubject} onChange={(event) => setSuspendSubject(event.target.value)} placeholder="Identificador sub del usuario" required /><input value={suspendReason} onChange={(event) => setSuspendReason(event.target.value)} placeholder="Motivo de la suspensión" maxLength={500} required /><button className="abandon-match-button" disabled={adminBusy}>Suspender permanentemente</button></form></section>
    {adminError && <p className="error-message" role="alert">{adminError}</p>}
  </section></main>

  if (!isAuthenticated) {
    return <main className="login-page">
      <section className="auth-card login-card">
        <h1>Bienvenid@</h1>
        <img className="login-brand-image" src="/restIcon.jpg" alt="Emblema del juego" />
        <p className="supporting-text">Inicia sesión para entrar a tu cuenta y jugar</p>
        <button className="primary-button" onClick={() => loginWithRedirect()}>Iniciar sesión</button>
      </section>
    </main>
  }

  return (
    <main className="auth-page">
      <section className="auth-card lobby-card">
        <img key={lobbyBanners[lobbyBannerIndex]} className="lobby-banner" src={lobbyBanners[lobbyBannerIndex]} alt="Escena de la taberna" />
        <p className="eyebrow">Gentlemen lobby</p>
        <div className="lobby-content">
            <p className="supporting-text">Sesión iniciada{user?.name ? ` como ${user.name}` : ''}</p>
            <div className="connection-panel">
              <div className="connection-heading"><span className={`status-dot status-${hubStatus}`} /><span>Servidor de juego</span><span className="status-label">{hubStatus === 'connected' ? 'Conectado' : hubStatus === 'connecting' ? 'Conectando' : hubStatus === 'error' ? 'Error' : 'Desconectado'}</span></div>
              {hubError && <p className="connection-error" role="alert">{hubError}</p>}
            </div>
            <section className="room-panel">
              <h2>Emparejamiento rápido</h2>
              <p className="supporting-text">{queueStatus === 'queued' ? 'Buscando rival…' : queueStatus === 'cancelled' ? 'Búsqueda cancelada.' : queueStatus === 'timedOut' ? 'No se encontró rival dentro del tiempo de espera.' : queueStatus === 'matched' ? '¡Rival encontrado!' : 'Encuentra otro jugador automáticamente'}</p>
              {quickMatchId && <p className="room-id">Match ID: {quickMatchId}</p>}
              {matchSnapshot && <p>Ronda {matchSnapshot.roundNumber} · Tu salud: {matchSnapshot.you.health} · Energía: {matchSnapshot.you.energy} · Salud rival: {matchSnapshot.opponent.health}</p>}
              {queueStatus === 'queued' ? <button className="secondary-button" onClick={cancelQuickMatch}>Cancelar búsqueda</button> : <button className="secondary-button" onClick={joinQuickMatch} disabled={!hubConnection || queueStatus === 'matched'}>Buscar rival</button>}
            </section>
            {room ? <section className="room-panel">
              <div className="section-heading"><h2>Tu sala</h2><button className="text-button" onClick={refreshRoom}>Actualizar</button></div><p className="room-id">ID: {room.roomId}</p>
              <p>Estado: <strong>{room.status}</strong> · Jugadores: {room.playerCount}/{room.capacity}</p>
              {createdInviteCode && <p>Código de invitación: <strong>{createdInviteCode}</strong></p>}
              {room.players.map((player) => <p key={player.playerId}>Jugador {player.isOwner ? '(anfitrión)' : ''}: {player.isReady ? 'listo' : 'esperando'}</p>)}
              {room.matchId ? <div className="match-started"><p>{room.status === 'Partida finalizada' ? 'Partida finalizada' : '¡Partida iniciada!'}</p><p className="room-id">Match ID: {room.matchId}</p></div> : <button className="secondary-button" onClick={() => setReady(!room.isReady)}>{room.isReady ? 'Cancelar listo' : 'Estoy listo'}</button>}
              <button className="text-button" onClick={leaveRoom}>Salir de la sala</button>
            </section> : <>
              <section className="room-panel">
                <h2>Crear sala</h2>
                <div className="form-row">
                  <div className="visibility-switch" role="group" aria-label="Visibilidad de la sala">
                    <button
                      type="button"
                      className={`visibility-option ${visibility === 'public' ? 'selected' : ''}`}
                      aria-pressed={visibility === 'public'}
                      onClick={() => setVisibility('public')}
                    >
                      Pública
                    </button>
                    <button
                      type="button"
                      className={`visibility-option ${visibility === 'private' ? 'selected' : ''}`}
                      aria-pressed={visibility === 'private'}
                      onClick={() => setVisibility('private')}
                    >
                      Privada
                    </button>
                  </div>
                  <button className="secondary-button compact-button" onClick={createRoom}>Crear</button>
                </div>
              </section>
              <section className="room-panel">
                <div className="section-heading"><h2>Salas públicas</h2><button className="text-button" onClick={loadPublicRooms}>Actualizar</button></div>
                {publicRooms.length === 0 ? <p className="supporting-text">No hay salas cargadas. Pulsa Actualizar.</p> : publicRooms.map((publicRoom) => <div className="room-row" key={publicRoom.roomId}><span>{publicRoom.playerCount}/{publicRoom.capacity} jugadores · {publicRoom.status}</span><button className="text-button" onClick={() => joinRoom(publicRoom.roomId)}>Unirse</button></div>)}
              </section>
              <section className="room-panel">
                <h2>Unirse con invitación</h2><input value={joinRoomId} onChange={(event) => setJoinRoomId(event.target.value)} placeholder="ID de sala" /><input value={inviteCode} onChange={(event) => setInviteCode(event.target.value)} placeholder="Código de invitación" /><button className="secondary-button" onClick={() => joinRoom(joinRoomId, inviteCode)} disabled={!joinRoomId.trim()}>Unirse a sala privada</button>
              </section>
            </>}
            {roomMessage && <p className="success-message" role="status">{roomMessage}</p>}
            {roomError && <p className="error-message" role="alert">{roomError}</p>}
            <section className="room-panel history-panel">
              <div className="section-heading"><h2>Historial de partidas</h2><button className="text-button" onClick={() => { const opening = !historyOpen; setHistoryOpen(opening); if (opening) void loadHistory() }}>{historyOpen ? 'Ocultar' : 'Ver historial'}</button></div>
              {historyOpen && <>
                {historyLoading && <p className="supporting-text">Cargando historial…</p>}
                {historyError && <p className="error-message" role="alert">{historyError}</p>}
                {!historyLoading && !historyError && history?.items.length === 0 && <p className="supporting-text">Aún no tienes partidas registradas.</p>}
                {!historyLoading && !historyError && history && history.items.length > 0 && <>
                  <div className="history-list">{history.items.map((entry) => <article className="history-entry" key={entry.matchId}>
                    <div><strong>{entry.opponentNickname || 'Rival'}</strong><span>{new Date(entry.completedAt ?? entry.createdAt).toLocaleString()}</span></div>
                    <div className="history-entry-meta"><span className={`history-result result-${entry.result}`}>{entry.result === 'won' ? 'Victoria' : entry.result === 'lost' ? 'Derrota' : entry.result === 'draw' ? 'Empate' : 'En curso'}</span><span>Ronda {entry.roundNumber}</span></div>
                  </article>)}</div>
                  <div className="history-pagination"><button className="secondary-button" onClick={() => void loadHistory(history.page - 1)} disabled={history.page <= 1 || historyLoading}>Anterior</button><span>{(history.page - 1) * history.pageSize + 1}–{Math.min(history.page * history.pageSize, history.total)} de {history.total}</span><button className="secondary-button" onClick={() => void loadHistory(history.page + 1)} disabled={history.page * history.pageSize >= history.total || historyLoading}>Siguiente</button></div>
                </>}
              </>}
            </section>
            {isAdmin && <button className="secondary-button admin-entry-button" onClick={() => { setShowAdmin(true); void refreshAdminData() }}>Administración</button>}
            <button className="primary-button" onClick={() => logout({ logoutParams: { returnTo: window.location.origin } })}>Cerrar sesión</button>
        </div>
      </section>
    </main>
  )
}

export default App
