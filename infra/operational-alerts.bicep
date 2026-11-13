targetScope = 'resourceGroup'

@description('Existing Azure Container App to monitor.')
param containerAppName string = 'gameproj-dev-app'

@description('Email receiver for operational alerts. Required explicitly when this isolated template is deployed.')
@minLength(3)
param alertEmail string

@description('Safety switch. This template is intentionally disconnected from main.bicep and creates nothing by default.')
param deployAlerts bool = false

@description('Number of HTTP 5xx responses in five minutes that triggers the alert.')
@minValue(1)
param http5xxThreshold int = 5

resource containerApp 'Microsoft.App/containerApps@2025-07-01' existing = {
  name: containerAppName
}

resource operationsActionGroup 'Microsoft.Insights/actionGroups@2023-01-01' = if (deployAlerts) {
  name: '${containerAppName}-operations'
  location: 'global'
  properties: {
    groupShortName: 'gameops'
    enabled: true
    emailReceivers: [
      {
        name: 'project-operator'
        emailAddress: alertEmail
        useCommonAlertSchema: true
      }
    ]
  }
}

resource http5xxAlert 'Microsoft.Insights/metricAlerts@2018-03-01' = if (deployAlerts) {
  name: '${containerAppName}-http-5xx'
  location: 'global'
  properties: {
    description: 'More than the approved number of HTTP 5xx responses in a five-minute window.'
    severity: 2
    enabled: true
    scopes: [containerApp.id]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT5M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          name: 'Http5xxResponses'
          metricNamespace: 'Microsoft.App/containerapps'
          metricName: 'Requests'
          operator: 'GreaterThan'
          threshold: http5xxThreshold
          timeAggregation: 'Total'
          criterionType: 'StaticThresholdCriterion'
          dimensions: [
            {
              name: 'statusCodeCategory'
              operator: 'Include'
              values: ['5xx']
            }
          ]
          skipMetricValidation: false
        }
      ]
    }
    autoMitigate: true
    targetResourceType: 'Microsoft.App/containerApps'
    actions: [
      {
        actionGroupId: operationsActionGroup!.id
      }
    ]
  }
}

resource noReplicaAlert 'Microsoft.Insights/metricAlerts@2018-03-01' = if (deployAlerts) {
  name: '${containerAppName}-no-ready-replica'
  location: 'global'
  properties: {
    description: 'The Container App reports fewer than one replica during a five-minute window.'
    severity: 1
    enabled: true
    scopes: [containerApp.id]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT5M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          name: 'NoReplica'
          metricNamespace: 'Microsoft.App/containerapps'
          metricName: 'Replicas'
          operator: 'LessThan'
          threshold: 1
          timeAggregation: 'Average'
          criterionType: 'StaticThresholdCriterion'
          dimensions: []
          skipMetricValidation: false
        }
      ]
    }
    autoMitigate: true
    targetResourceType: 'Microsoft.App/containerApps'
    actions: [
      {
        actionGroupId: operationsActionGroup!.id
      }
    ]
  }
}

output alertDeploymentEnabled bool = deployAlerts
