# exit when any command fails
set -e -x

# global resources deploy only in us-west-2!
aws cloudformation package \
  --template-file templates/global.template \
  --s3-bucket ${BUCKET_NAME_PREFIX}-us-west-2 \
  --s3-prefix ${BUCKET_KEY_PREFIX}/email-origin/${CODEBUILD_RESOLVED_SOURCE_VERSION} \
  --region us-west-2 \
  > global.template.published

# deploy to all of the regions enabled in the account
export ACCOUNT_REGIONS=$(aws account list-regions \
  --region-opt-status-contains "ENABLED" "ENABLED_BY_DEFAULT" \
  --no-paginate \
  --query "Regions[].RegionName" \
  --output text|tr -s '[:blank:]' '[,*]')

aws cloudformation deploy \
  --stack-name email-origin-global \
  --template-file global.template.published \
  --no-fail-on-empty-changeset \
  --parameter-overrides \
    ReplicaRegions="${REPLICA_REGIONS:-ACCOUNT_REGIONS}" \
  --capabilities CAPABILITY_IAM CAPABILITY_AUTO_EXPAND \
  --region us-west-2
