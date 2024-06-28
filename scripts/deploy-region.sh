# exit when any command fails
set -e -x

#get global stack output for EB_ROLE_ARN from EventBridgeRoleArn (in us-west-2)
aws cloudformation describe-stacks \
  --stack-name email-origin-global \
  --query "Stacks[0].Outputs" \
  --region us-west-2 \
  > global-stack-output.json

more global-stack-output.json

export API_GATEWAY_INT_ROLE_ARN=$(jq 'map(select(.OutputKey == "ApiGatewayIntegrationRoleArn"))[0]|.OutputValue' -r global-stack-output.json)
export PROCESSOR_LAMBDA_ROLE_ARN=$(jq 'map(select(.OutputKey == "ProcessorLambdaRoleArn"))[0]|.OutputValue' -r global-stack-output.json)
export SENDER_LAMBDA_ROLE_ARN=$(jq 'map(select(.OutputKey == "SenderLambdaRoleArn"))[0]|.OutputValue' -r global-stack-output.json)
export API_HANDLER_ROLE_ARN=$(jq 'map(select(.OutputKey == "ApiHandlerRoleArn"))[0]|.OutputValue' -r global-stack-output.json)
export WORKER_ROLE_ARN=$(jq 'map(select(.OutputKey == "WorkerRoleArn"))[0]|.OutputValue' -r global-stack-output.json)
export SENDER_ROLE_ARN=$(jq 'map(select(.OutputKey == "SenderRoleArn"))[0]|.OutputValue' -r global-stack-output.json)
export EVENT_BRIDGE_ROLE_ARN=$(jq 'map(select(.OutputKey == "EventBridgeRoleArn"))[0]|.OutputValue' -r global-stack-output.json)

export GLOBAL_TABLE_NAME=$(jq 'map(select(.OutputKey == "GlobalTableName"))[0]|.OutputValue' -r global-stack-output.json)
export GLOBAL_TABLE_ARN=$(jq 'map(select(.OutputKey == "GlobalTableArn"))[0]|.OutputValue' -r global-stack-output.json)

# package resources
sam build --template-file templates/regional.template

# deploy to all of the regions enabled in the account
export ACCOUNT_REGIONS=$(aws account list-regions \
  --region-opt-status-contains "ENABLED" "ENABLED_BY_DEFAULT" \
  --no-paginate \
  --query "Regions[].RegionName" \
  --output text)

for DEPLOY_REGION in ${DEPLOY_TO_REGIONS:-ACCOUNT_REGIONS}; do
  sam deploy \
    --s3-bucket ${BUCKET_NAME_PREFIX}-${DEPLOY_REGION} \
    --s3-prefix ${BUCKET_KEY_PREFIX}/email-origin/${CODEBUILD_RESOLVED_SOURCE_VERSION} \
    --stack-name email-origin \
    --parameter-overrides \
      Table=${GLOBAL_TABLE_NAME} \
      ApiGatewayIntegrationRoleArn=${API_GATEWAY_INT_ROLE_ARN} \
      ApiHandlerRoleArn=${API_HANDLER_ROLE_ARN} \
      ProcessorLambdaRoleArn=${PROCESSOR_LAMBDA_ROLE_ARN} \
      SenderLambdaRoleArn=${SENDER_LAMBDA_ROLE_ARN} \
      WorkerRoleArn=${WORKER_ROLE_ARN} \
      SenderRoleArn=${SENDER_ROLE_ARN} \
      EventBridgeRoleArn=${EVENT_BRIDGE_ROLE_ARN} \
    --capabilities CAPABILITY_IAM CAPABILITY_AUTO_EXPAND \
    --region ${DEPLOY_REGION}
done